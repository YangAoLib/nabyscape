using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace NabyScape;

internal sealed record WeatherPlace(string Name,double Latitude,double Longitude,string StationCode="");
internal sealed class WeatherHour
{
    public string Time {get;set;}="";
    public double Temperature {get;set;}
    public int Code {get;set;}
    public bool Day {get;set;}
    public double RainChance {get;set;}
}
internal sealed class WeatherSnapshot
{
    public string Key {get;set;}="";
    public DateTimeOffset Updated {get;set;}
    public double Temperature {get;set;}
    public double FeelsLike {get;set;}
    public double Wind {get;set;}
    public double Humidity {get;set;}
    public double High {get;set;}
    public double Low {get;set;}
    public double RainChance {get;set;}
    public int Code {get;set;}
    public bool Day {get;set;}
    public string Sunrise {get;set;}="";
    public string Sunset {get;set;}="";
    public string ConditionText {get;set;}="";
    public List<WeatherHour> Hours {get;set;}=new();
}
internal sealed class WeatherService : IDisposable
{
    private static readonly HttpClient client=new(){Timeout=TimeSpan.FromSeconds(15),MaxResponseContentBufferSize=1024*1024};
    private readonly Store store;
    private readonly CancellationTokenSource shutdown=new();
    // NMC province directory codes from https://www.nmc.cn/publish/forecast.html.
    private static readonly string[] NmcProvinces={"ABJ","ATJ","AHE","ASX","ALN","AJL","AHL","ASH","AJS","AZJ","AAH","AFJ","AJX","ASD","AHA","AHB","AHN","AGD","AGX","AHI","ACQ","ASC","AGZ","AYN","AXZ","ASN","AGS","AQH","ANX","AXJ","AXG","AAM","ATW"};
    private static List<WeatherPlace>? nmcStations;
    private sealed class ForecastRequest
    {
        internal required CancellationTokenSource Cancellation;
        internal required Task<WeatherSnapshot> Task;
        internal int Users;
    }
    private ForecastRequest? pending;
    private string pendingKey="";
    internal WeatherSnapshot? Cached {get;private set;}
    internal DateTimeOffset LastAttempt {get;private set;}
    internal WeatherService(Store store){this.store=store;Cached=store.Load<WeatherSnapshot>("weather");}
    internal static string Key(Settings s)=>s.WeatherSource=="nmc"
        ?s.WeatherStationCode.Length>0?"nmc|"+s.WeatherStationCode+"|"+s.WeatherUnits:""
        :s.WeatherLatitude is {} lat && s.WeatherLongitude is {} lon?FormattableString.Invariant($"{lat:F5},{lon:F5}|{s.WeatherUnits}"):"";
    internal async Task<WeatherPlace[]> Search(Settings settings,string city,CancellationToken token)
    {
        city=city.Trim();if(city.Length<2)return Array.Empty<WeatherPlace>();if(city.Length>120)city=city[..120];
        if(settings.WeatherSource=="nmc")return await SearchNmc(city,token);
        using var doc=await Fetch("https://geocoding-api.open-meteo.com/v1/search?name="+Uri.EscapeDataString(city)+"&count=8&language=en&format=json",token);
        if(!doc.RootElement.TryGetProperty("results",out var results))return Array.Empty<WeatherPlace>();
        return results.EnumerateArray().Select(place=>new WeatherPlace(string.Join(", ",new[]{Text(place,"name"),Text(place,"admin1"),Text(place,"country")}.Where(v=>v.Length>0).Distinct()),Number(place,"latitude"),Number(place,"longitude"))).Where(p=>Math.Abs(p.Latitude)<=90 && Math.Abs(p.Longitude)<=180).ToArray();
    }
    internal async Task<WeatherSnapshot> Get(Settings s,bool force,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();string key=Key(s);if(key.Length==0)throw new InvalidOperationException("Choose a city first.");
        if(!force && Cached is {} saved && saved.Key==key && DateTimeOffset.UtcNow-saved.Updated<TimeSpan.FromMinutes(20))return saved;
        var request=pending;
        if(request==null || request.Task.IsCompleted || request.Cancellation.IsCancellationRequested || pendingKey!=key)
        {
            pendingKey=key;LastAttempt=DateTimeOffset.UtcNow;var cancellation=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            request=new(){Cancellation=cancellation,Task=s.WeatherSource=="nmc"
                ?DownloadNmc(s.WeatherStationCode,s.WeatherUnits,key,cancellation.Token)
                :Download(s.WeatherLatitude!.Value,s.WeatherLongitude!.Value,s.WeatherUnits,key,cancellation.Token)};pending=request;
        }
        request.Users++;
        try{return await request.Task.WaitAsync(token);}
        finally
        {
            if(--request.Users==0)
            {
                if(!request.Task.IsCompleted)request.Cancellation.Cancel();
                request.Cancellation.Dispose();if(ReferenceEquals(pending,request))pending=null;
            }
        }
    }
    private async Task<WeatherSnapshot> Download(double lat,double lon,string units,string key,CancellationToken token)
    {
        string coordinates=FormattableString.Invariant($"latitude={lat}&longitude={lon}");
        string url="https://api.open-meteo.com/v1/forecast?"+coordinates+"&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m&hourly=temperature_2m,weather_code,is_day,precipitation_probability&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset&timezone=auto&forecast_days=2&temperature_unit="+(units=="Fahrenheit"?"fahrenheit":"celsius");
        using var doc=await Fetch(url,token);var root=doc.RootElement;var current=root.GetProperty("current");var daily=root.GetProperty("daily");var hourly=root.GetProperty("hourly");
        var snapshot=new WeatherSnapshot{Key=key,Updated=DateTimeOffset.UtcNow,Temperature=Number(current,"temperature_2m"),FeelsLike=Number(current,"apparent_temperature"),Wind=Number(current,"wind_speed_10m"),Humidity=Number(current,"relative_humidity_2m"),Code=(int)Number(current,"weather_code"),Day=Number(current,"is_day")==1,High=At(daily,"temperature_2m_max",0),Low=At(daily,"temperature_2m_min",0),RainChance=At(daily,"precipitation_probability_max",0),Sunrise=daily.GetProperty("sunrise")[0].GetString()??"",Sunset=daily.GetProperty("sunset")[0].GetString()??""};
        string now=Text(current,"time");var times=hourly.GetProperty("time");
        for(int i=0;i<times.GetArrayLength() && snapshot.Hours.Count<6;i++)
        {
            string time=times[i].GetString()??"";if(string.CompareOrdinal(time,now)<0)continue;
            snapshot.Hours.Add(new(){Time=time.Length>=16?time.Substring(11,5):time,Temperature=At(hourly,"temperature_2m",i),Code=(int)At(hourly,"weather_code",i),Day=At(hourly,"is_day",i)==1,RainChance=At(hourly,"precipitation_probability",i)});
        }
        if(!token.IsCancellationRequested && pendingKey==key){Cached=snapshot;try{store.Save("weather",snapshot);}catch(Exception ex){store.Log(ex);}}return snapshot;
    }
    // NMC (中央气象台) source: works in mainland China without an API key.
    private async Task<WeatherSnapshot> DownloadNmc(string station,string units,string key,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(station))throw new InvalidOperationException("Choose a city first.");
        using var doc=await FetchNmc("https://www.nmc.cn/rest/weather?stationid="+Uri.EscapeDataString(station),token);
        var root=doc.RootElement;if(!root.TryGetProperty("data",out var data)||data.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("NMC response unavailable.");
        var real=data.GetProperty("real");var weather=real.GetProperty("weather");
        double celsius=Number(weather,"temperature");
        string sunrise=Clock(Text(real.GetProperty("sunriseSunset"),"sunrise")),sunset=Clock(Text(real.GetProperty("sunriseSunset"),"sunset"));
        string now=DateTimeOffset.Now.ToString("HH:mm");
        bool day=string.CompareOrdinal(now,sunrise)>=0 && string.CompareOrdinal(now,sunset)<0;
        var snapshot=new WeatherSnapshot{Key=key,Updated=DateTimeOffset.UtcNow,Temperature=Convert(celsius,units),FeelsLike=Convert(Number(weather,"feelst"),units),Wind=WindKmh(Text(real.GetProperty("wind"),"power")),Humidity=Number(weather,"humidity"),Code=NmcToCode(Number(weather,"img")),Day=day,Sunrise=sunrise,Sunset=sunset};
        string info=Text(weather,"info");snapshot.ConditionText=info is "-" or "" ?"":info;
        if(data.TryGetProperty("predict",out var predict)&&predict.TryGetProperty("detail",out var detail)&&detail.ValueKind==JsonValueKind.Array&&detail.GetArrayLength()>0)
        {
            var today=detail[0];
            double dayTemp=HalfTemp(today,true),nightTemp=HalfTemp(today,false);
            snapshot.High=Convert(Math.Max(dayTemp,nightTemp),units);snapshot.Low=Convert(Math.Min(dayTemp,nightTemp),units);
            // NMC has no hourly forecast; fill the strip with day/night half-day slots.
            (int Index,string Label,bool IsDay)[] slots=day
                ?new[]{(0,"今天",true),(0,"今晚",false),(1,"明白天",true),(1,"明夜",false)}
                :new[]{(0,"今晚",false),(1,"明白天",true),(1,"明夜",false),(2,"后天",true)};
            foreach(var slot in slots)
            {
                if(slot.Index>=detail.GetArrayLength())break;
                var half=detail[slot.Index].GetProperty(slot.IsDay?"day":"night").GetProperty("weather");
                snapshot.Hours.Add(new(){Time=slot.Label,Temperature=Convert(HalfTemp(detail[slot.Index],slot.IsDay),units),Code=NmcToCode(Number(half,"img")),Day=slot.IsDay,RainChance=0});
            }
        }
        if(!token.IsCancellationRequested && pendingKey==key){Cached=snapshot;try{store.Save("weather",snapshot);}catch(Exception ex){store.Log(ex);}}return snapshot;
    }
    private static double HalfTemp(JsonElement day,bool daytime)
        =>double.TryParse(Text(day.GetProperty(daytime?"day":"night").GetProperty("weather"),"temperature"),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value)?value:0;
    private static string Clock(string value)=>value.Length>=16?value.Substring(11,5):"";
    // Beaufort-like Chinese wind levels or named levels to km/h.
    private static double WindKmh(string power)
    {
        var digits=System.Text.RegularExpressions.Regex.Matches(power??"",@"\d+");
        if(digits.Count>0&&int.TryParse(digits[^1].Value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var level)&&level is >=0 and <=12)return Math.Min(120,level*9);
        return power?.Trim() switch{"无风"=>0,"软风"=>3,"轻风"=>8,"微风"=>16,"和风"=>24,"劲风"=>34,"强风"=>44,"疾风"=>55,"大风"=>68,"烈风"=>81,"狂风"=>95,"暴风"=>110,"飓风"=>120,_=>12};
    }
    private static double Convert(double celsius,string units)=>units=="Fahrenheit"?celsius*9d/5d+32d:celsius;
    // NMC icon codes (0 晴 1 多云 2 阴 3 阵雨 4 雷阵雨 5 雷阵雨伴冰雹 6-11 雨 12-16 雪 17 雾 18 冻雨 19-31 沙尘 53 霾 …) to WMO weather codes.
    private static int NmcToCode(double raw)
    {
        int img=(int)raw;
        return img switch{0=>0,1=>1,2=>3,3=>80,4 or 5=>95,6=>61,7=>63,8 or 9=>65,10 or 11=>82,12=>85,13=>71,14=>73,15 or 16=>75,17 or 28 or 32 or 33 or 34 or 53=>45,18=>67,20=>61,21=>63,22 or 23=>65,24=>82,25=>71,26=>73,27=>75,19 or 29 or 30 or 31=>45,_=>0};
    }
    private static async Task<WeatherPlace[]> SearchNmc(string city,CancellationToken token)
    {
        var index=await NmcStationIndex(token);
        return index.Where(p=>p.Name.Contains(city,StringComparison.OrdinalIgnoreCase)||p.StationCode.Equals(city,StringComparison.OrdinalIgnoreCase)).Take(8).ToArray();
    }
    private static async Task<List<WeatherPlace>> NmcStationIndex(CancellationToken token)
    {
        if(nmcStations is {} cached)return cached;
        var bag=new System.Collections.Concurrent.ConcurrentBag<WeatherPlace>();
        await Task.WhenAll(NmcProvinces.Select(async province=>
        {
            try
            {
                using var doc=await FetchNmc("https://www.nmc.cn/rest/province/"+province,token);
                if(doc.RootElement.ValueKind!=JsonValueKind.Array)return;
                foreach(var item in doc.RootElement.EnumerateArray())
                {
                    string code=Text(item,"code"),name=Text(item,"city"),region=Text(item,"province");
                    if(code.Length==0||name.Length==0)continue;
                    region=region.Replace("省","").Replace("市","").Replace("壮族自治区","").Replace("回族自治区","").Replace("维吾尔自治区","").Replace("自治区","").Replace("特别行政区","");
                    bag.Add(new WeatherPlace(region.Length>0&&region!=name?name+" · "+region:name,0,0,code));
                }
            }
            catch(OperationCanceledException){throw;}
            catch(Exception){/* a single province failing must not break the whole index. */}
        }));
        if(bag.IsEmpty)throw new InvalidOperationException("NMC station list unavailable.");
        nmcStations=bag.DistinctBy(p=>p.StationCode).OrderBy(p=>p.Name,StringComparer.OrdinalIgnoreCase).ToList();
        return nmcStations;
    }
    private static async Task<JsonDocument> FetchNmc(string url,CancellationToken token)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,url);
        request.Headers.TryAddWithoutValidation("Referer","https://www.nmc.cn/");
        request.Headers.TryAddWithoutValidation("User-Agent","Mozilla/5.0 (Windows NT 10.0; Win64; x64) NabyScape");
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream,default,token);
    }
    private static async Task<JsonDocument> Fetch(string url,CancellationToken token)
    {
        using var response=await client.GetAsync(url,token);response.EnsureSuccessStatusCode();return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }
    private static double Number(JsonElement value,string name)=>value.TryGetProperty(name,out var field) && field.ValueKind==JsonValueKind.Number && field.TryGetDouble(out var n) && double.IsFinite(n)?n:0;
    private static double At(JsonElement value,string name,int index)=>value.TryGetProperty(name,out var field) && field.ValueKind==JsonValueKind.Array && index<field.GetArrayLength() && field[index].ValueKind==JsonValueKind.Number && field[index].TryGetDouble(out var n) && double.IsFinite(n)?n:0;
    private static string Text(JsonElement value,string name)=>value.TryGetProperty(name,out var field) && field.ValueKind==JsonValueKind.String?field.GetString()??"":"";
    internal static string Condition(int code)=>code switch{0=>"Clear skies",1=>"Mostly clear",2=>"Partly cloudy",3=>"Cloudy",45 or 48=>"Foggy",>=51 and <=57=>"Drizzle",>=61 and <=67=>"Rain",>=71 and <=77=>"Snow",>=80 and <=82=>"Rain showers",85 or 86=>"Snow showers",>=95=>"Thunderstorms",_=>"Weather"};
    internal static string ConditionText(WeatherSnapshot data)=>data.ConditionText.Length>0?data.ConditionText:Condition(data.Code);
    public void Dispose(){shutdown.Cancel();shutdown.Dispose();}
}
