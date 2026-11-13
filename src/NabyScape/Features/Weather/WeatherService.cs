using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
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
    private static readonly HttpClient client=new(new SocketsHttpHandler{AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate})
        {Timeout=TimeSpan.FromSeconds(15),MaxResponseContentBufferSize=1024*1024};
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
    internal static string Key(Settings s)
    {
        switch(s.WeatherSource)
        {
            case "nmc":return s.WeatherStationCode.Length>0?"nmc|"+s.WeatherStationCode+"|"+s.WeatherUnits:"";
            case "qweather":
                (string host,string key)=QwCredential(s);
                if(s.WeatherAuthMode=="jwt")
                    return s.WeatherStationCode.Length>0&&host.Length>0&&s.WeatherProjectId.Length>0&&s.WeatherCredentialId.Length>0&&s.WeatherPrivateKey.Length>0
                        ?"qw|"+s.WeatherStationCode+"|"+s.WeatherUnits+"|jwt|"+QwKeyPart(host,s.WeatherProjectId+"."+s.WeatherCredentialId):"";
                return s.WeatherStationCode.Length>0&&host.Length>0&&key.Length>0?"qw|"+s.WeatherStationCode+"|"+s.WeatherUnits+"|"+QwKeyPart(host,key):"";
            default:return s.WeatherLatitude is {} lat && s.WeatherLongitude is {} lon?FormattableString.Invariant($"{lat:F5},{lon:F5}|{s.WeatherUnits}"):"";
        }
    }
    // QWeather requires the dedicated API Host every account gets from its console; a "host|key" value pasted into the key box from older builds still works.
    internal static (string Host,string Key) QwCredential(Settings s)
    {
        string host=s.WeatherApiHost.Trim(),key=s.WeatherApiKey.Trim();
        int split=key.IndexOf('|');
        if(split>0){host=key[..split].Trim();key=key[(split+1)..].Trim();}
        return (host,key);
    }
    private static string QwKeyPart(string host,string key)=>(host.Length>12?host[..12]:host)+"|"+(key.Length>4?key[..4]:key);
    internal async Task<WeatherPlace[]> Search(Settings settings,string city,CancellationToken token)
    {
        city=city.Trim();if(city.Length<2)return Array.Empty<WeatherPlace>();if(city.Length>120)city=city[..120];
        if(settings.WeatherSource=="nmc")return await SearchNmc(city,token);
        if(settings.WeatherSource=="qweather")
        {
            (string host,string key)=QwCredential(settings);
            if(host.Length==0)return Array.Empty<WeatherPlace>();
            if(settings.WeatherAuthMode=="jwt"){if(!JwtConfigured(settings))return Array.Empty<WeatherPlace>();}
            else if(key.Length==0)return Array.Empty<WeatherPlace>();
            using var doc=await FetchQw(settings,host,"/geo/v2/city/lookup?location="+Uri.EscapeDataString(city)+"&number=8",token);
            if(!doc.RootElement.TryGetProperty("location",out var results)||results.ValueKind!=JsonValueKind.Array)return Array.Empty<WeatherPlace>();
            return results.EnumerateArray().Select(place=>new WeatherPlace(string.Join(" · ",new[]{Text(place,"name"),Text(place,"adm1")}.Where(v=>v.Length>0).Distinct()),0,0,Text(place,"id"))).Where(p=>p.StationCode.Length>0).ToArray();
        }
        using var doc2=await Fetch("https://geocoding-api.open-meteo.com/v1/search?name="+Uri.EscapeDataString(city)+"&count=8&language=en&format=json",token);
        if(!doc2.RootElement.TryGetProperty("results",out var results2))return Array.Empty<WeatherPlace>();
        return results2.EnumerateArray().Select(place=>new WeatherPlace(string.Join(", ",new[]{Text(place,"name"),Text(place,"admin1"),Text(place,"country")}.Where(v=>v.Length>0).Distinct()),Number(place,"latitude"),Number(place,"longitude"))).Where(p=>Math.Abs(p.Latitude)<=90 && Math.Abs(p.Longitude)<=180).ToArray();
    }
    internal async Task<WeatherSnapshot> Get(Settings s,bool force,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();string key=Key(s);if(key.Length==0)throw new InvalidOperationException("Choose a city first.");
        if(!force && Cached is {} saved && saved.Key==key && DateTimeOffset.UtcNow-saved.Updated<TimeSpan.FromMinutes(20))return saved;
        var request=pending;
        if(request==null || request.Task.IsCompleted || request.Cancellation.IsCancellationRequested || pendingKey!=key)
        {
            pendingKey=key;LastAttempt=DateTimeOffset.UtcNow;var cancellation=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            request=new(){Cancellation=cancellation,Task=s.WeatherSource switch
            {
                "nmc"=>DownloadNmc(s.WeatherStationCode,s.WeatherUnits,key,cancellation.Token),
                "qweather"=>DownloadQweather(s,key,cancellation.Token),
                _=>Download(s.WeatherLatitude!.Value,s.WeatherLongitude!.Value,s.WeatherUnits,key,cancellation.Token)
            }};pending=request;
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
        var snapshot=new WeatherSnapshot{Key=key,Updated=DateTimeOffset.UtcNow,Temperature=ConvertTemp(celsius,units),FeelsLike=ConvertTemp(Number(weather,"feelst"),units),Wind=WindKmh(Text(real.GetProperty("wind"),"power")),Humidity=Number(weather,"humidity"),Code=NmcToCode(Number(weather,"img")),Day=day,Sunrise=sunrise,Sunset=sunset};
        string info=Text(weather,"info");snapshot.ConditionText=info is "-" or "" ?"":info;
        if(data.TryGetProperty("predict",out var predict)&&predict.TryGetProperty("detail",out var detail)&&detail.ValueKind==JsonValueKind.Array&&detail.GetArrayLength()>0)
        {
            var today=detail[0];
            double dayTemp=HalfTemp(today,true),nightTemp=HalfTemp(today,false);
            snapshot.High=ConvertTemp(Math.Max(dayTemp,nightTemp),units);snapshot.Low=ConvertTemp(Math.Min(dayTemp,nightTemp),units);
            // NMC has no hourly forecast; fill the strip with day/night half-day slots.
            (int Index,string Label,bool IsDay)[] slots=day
                ?new[]{(0,"今天",true),(0,"今晚",false),(1,"明白天",true),(1,"明夜",false)}
                :new[]{(0,"今晚",false),(1,"明白天",true),(1,"明夜",false),(2,"后天",true)};
            foreach(var slot in slots)
            {
                if(slot.Index>=detail.GetArrayLength())break;
                var half=detail[slot.Index].GetProperty(slot.IsDay?"day":"night").GetProperty("weather");
                snapshot.Hours.Add(new(){Time=slot.Label,Temperature=ConvertTemp(HalfTemp(detail[slot.Index],slot.IsDay),units),Code=NmcToCode(Number(half,"img")),Day=slot.IsDay,RainChance=0});
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
    private static double ConvertTemp(double celsius,string units)=>units=="Fahrenheit"?celsius*9d/5d+32d:celsius;
    // NMC icon codes (0 晴 1 多云 2 阴 3 阵雨 4 雷阵雨 5 雷阵雨伴冰雹 6-11 雨 12-16 雪 17 雾 18 冻雨 19-31 沙尘 53 霾 …) to WMO weather codes.
    private static int NmcToCode(double raw)
    {
        int img=(int)raw;
        return img switch{0=>0,1=>1,2=>3,3=>80,4 or 5=>95,6=>61,7=>63,8 or 9=>65,10 or 11=>82,12=>85,13=>71,14=>73,15 or 16=>75,17 or 28 or 32 or 33 or 34 or 53=>45,18=>67,20=>61,21=>63,22 or 23=>65,24=>82,25=>71,26=>73,27=>75,19 or 29 or 30 or 31=>45,_=>0};
    }
    // QWeather (和风天气) source: hourly data with precipitation probability; API key or JWT credentials from qweather.com.
    private async Task<WeatherSnapshot> DownloadQweather(Settings s,string key,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(s.WeatherStationCode))throw new InvalidOperationException("Choose a city first.");
        (string host,string apiKeyValue)=QwCredential(s);
        if(host.Length==0)throw new InvalidOperationException(Localization.Text("QWeather API Host required. Paste the one your console assigns, for example abc.re.qweatherapi.com."));
        if(s.WeatherAuthMode!="jwt" && apiKeyValue.Length==0)throw new InvalidOperationException(Localization.Text("Enter your QWeather API key first."));
        string location=s.WeatherStationCode,units=s.WeatherUnits;
        string unit=units=="Fahrenheit"?"i":"m";
        using var now=await FetchQw(s,host,"/v7/weather/now?location="+Uri.EscapeDataString(location)+"&unit="+unit,token);
        var current=Section(now.RootElement,"now");
        string sunrise="",sunset="";
        using var daily=await FetchQw(s,host,"/v7/weather/7d?location="+Uri.EscapeDataString(location)+"&unit="+unit,token);
        if(TrySection(daily.RootElement,"daily",out var days)&&days.ValueKind==JsonValueKind.Array&&days.GetArrayLength()>0)
        {
            sunrise=Text(days[0],"sunrise");sunset=Text(days[0],"sunset");
        }
        string nowTime=DateTimeOffset.Now.ToString("HH:mm");
        bool day=sunrise.Length>=5&&sunset.Length>=5&&string.CompareOrdinal(nowTime,sunrise)>=0&&string.CompareOrdinal(nowTime,sunset)<0;
        var snapshot=new WeatherSnapshot{Key=key,Updated=DateTimeOffset.UtcNow,Temperature=Num(current,"temp"),FeelsLike=Num(current,"feelsLike"),Wind=Num(current,"windSpeed"),Humidity=Num(current,"humidity"),Code=QwToCode(Num(current,"icon")),Day=day,High=0,Low=0,RainChance=0,Sunrise=sunrise,Sunset=sunset};
        string info=Text(current,"text");snapshot.ConditionText=info;
        if(TrySection(daily.RootElement,"daily",out var forecast)&&forecast.ValueKind==JsonValueKind.Array&&forecast.GetArrayLength()>0)
        {
            snapshot.High=Num(forecast[0],"tempMax");snapshot.Low=Num(forecast[0],"tempMin");
        }
        try
        {
            using var hours=await FetchQw(s,host,"/v7/weather/24h?location="+Uri.EscapeDataString(location)+"&unit="+unit,token);
            if(TrySection(hours.RootElement,"hourly",out var hourly)&&hourly.ValueKind==JsonValueKind.Array)
            {
                double best=0;
                foreach(var hour in hourly.EnumerateArray().Take(6))
                {
                    string time=Text(hour,"fxTime");int at=time.IndexOf('T');
                    best=Math.Max(best,Num(hour,"pop"));
                    snapshot.Hours.Add(new(){Time=at>=0&&time.Length>=at+6?time.Substring(at+1,5):time,Temperature=Num(hour,"temp"),Code=QwToCode(Num(hour,"icon")),Day=DayFromTime(time,sunrise,sunset),RainChance=Num(hour,"pop")});
                }
                snapshot.RainChance=best;
            }
        }
        catch(OperationCanceledException){throw;}
        catch(Exception){/* hourly details are optional; the current conditions already downloaded. */}
        if(!token.IsCancellationRequested && pendingKey==key){Cached=snapshot;try{store.Save("weather",snapshot);}catch(Exception ex){store.Log(ex);}}return snapshot;
    }
    private static bool DayFromTime(string fxTime,string sunrise,string sunset)
    {
        int at=fxTime.IndexOf('T');if(at<0||fxTime.Length<at+6)return true;
        string time=fxTime.Substring(at+1,5);
        if(sunrise.Length<5||sunset.Length<5)return true;
        return string.CompareOrdinal(time,sunrise)>=0&&string.CompareOrdinal(time,sunset)<0;
    }
    private static JsonElement Section(JsonElement root,string name)
    {
        if(!root.TryGetProperty(name,out var section))throw new InvalidOperationException("QWeather response unavailable.");
        return section;
    }
    private static void CheckQw(JsonElement root)
    {
        string code=Text(root,"code");
        if(code.Length==0||code=="200")return;
        throw code is "401" or "402" or "403"
            ?new InvalidOperationException(Localization.Format("QWeather API key or plan issue ({0}). Check the credentials, and fill the API Host box with the host your console assigns.",code))
            :new InvalidOperationException(Localization.Format("QWeather error ({0}).",code));
    }
    private static bool TrySection(JsonElement root,string name,out JsonElement section)=>root.TryGetProperty(name,out section);
    // QWeather icon codes (100 晴 101 多云 104 阴 300-399 雨 400-499 雪 500-515 雾霾沙尘) to WMO weather codes.
    private static int QwToCode(double raw)
    {
        int icon=(int)raw;
        return icon switch{100 or 150=>0,102 or 103 or 152 or 153=>1,101 or 151=>2,104=>3,309=>51,305 or 314 or 350 or 399=>61,306 or 315=>63,307 or 308 or 310=>65,311 or 312 or 317 or 318=>82,300 or 301=>80,313 or 316=>67,400 or 408 or 499=>71,401 or 409=>73,402 or 403 or 410=>75,404 or 405 or 406 or 456=>68,407 or 457=>85,>=500 and <=515=>45,_=>icon>=900?0:icon==0?0:2};
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
    // QWeather rejects bad credentials with a bare HTTP 403/401 (empty body); surface an actionable message instead of a generic failure.
    // API-key mode appends the key to the query; JWT mode signs an EdDSA token (kid + sub + iat/exp) and sends it as a bearer token.
    private static string? jwtToken;
    private static DateTimeOffset jwtExpires;
    private static async Task<JsonDocument> FetchQw(Settings s,string host,string path,CancellationToken token)
    {
        (string _,string key)=QwCredential(s);
        string url="https://"+host+path+(path.Contains('?')?"&":"?")+"lang=zh";
        using var request=new HttpRequestMessage(HttpMethod.Get,url);
        if(s.WeatherAuthMode=="jwt")
        {
            request.Headers.TryAddWithoutValidation("Authorization","Bearer "+EnsureJwt(s));
        }
        else
        {
            if(key.Length==0)throw new InvalidOperationException(Localization.Text("Enter your QWeather API key first."));
            request.RequestUri=new Uri(url+"&key="+Uri.EscapeDataString(key));
        }
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        if(!response.IsSuccessStatusCode)
        {
            int status=(int)response.StatusCode;
            throw status is 401 or 403
                ?new InvalidOperationException(Localization.Format("QWeather rejected the request ({0}). Check the credentials, and fill the API Host box with the host your console assigns.",status))
                :new InvalidOperationException(Localization.Format("QWeather error ({0}).",status));
        }
        await using var stream=await response.Content.ReadAsStreamAsync(token);
        var doc=await JsonDocument.ParseAsync(stream,default,token);
        CheckQw(doc.RootElement);
        return doc;
    }
    private static bool JwtConfigured(Settings s)=>s.WeatherProjectId.Length>0&&s.WeatherCredentialId.Length>0&&s.WeatherPrivateKey.Length>0;
    // .NET has no public Ed25519 API, so PEM handling and EdDSA signatures use BouncyCastle.
    // RFC 8410 makes Ed25519 PKCS#8/SPKI fixed-size structures, so parsing is done directly on the DER
    // instead of relying on PemReader's version-dependent PKCS#8 object mapping.
    internal static Ed25519PrivateKeyParameters ImportPrivateKey(string pem)
    {
        byte[] der=PemDer(pem);
        if(der.Length==48 && der[0]==0x30 && der[1]==0x2E && der[12]==0x04 && der[14]==0x04 && der[15]==0x20)
            return new Ed25519PrivateKeyParameters(der,16);
        throw new InvalidOperationException(Localization.Text("QWeather private key is invalid."));
    }
    internal static bool JwtReady(Settings s)
    {
        try{ImportPrivateKey(s.WeatherPrivateKey);return true;}
        catch(Exception){return false;}
    }
    internal static Ed25519PublicKeyParameters ImportPublicKey(string pem)
    {
        byte[] der=PemDer(pem);
        if(der.Length==44 && der[0]==0x30 && der[1]==0x2A && der[12]==0x03 && der[13]==0x21)
            return new Ed25519PublicKeyParameters(der,14);
        throw new InvalidOperationException(Localization.Text("QWeather private key is invalid."));
    }
    private static byte[] PemDer(string pem)
    {
        if(!pem.Contains('-'))return Convert.FromBase64String(pem.Trim());
        string base64=string.Concat(pem.Split('\n').Where(line=>!line.Contains('-')).Select(line=>line.Trim()));
        return Convert.FromBase64String(base64);
    }
    private static string EnsureJwt(Settings s)
    {
        if(jwtToken is { } cached && DateTimeOffset.UtcNow<jwtExpires)return cached;
        if(!JwtConfigured(s))throw new InvalidOperationException(Localization.Text("Enter your QWeather project ID, credential ID and private key first."));
        Ed25519PrivateKeyParameters parameters=ImportPrivateKey(s.WeatherPrivateKey);
        long iat=DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds();
        long exp=DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        string header=Base64Url("{\"alg\":\"EdDSA\",\"kid\":\""+EscapeJson(s.WeatherCredentialId)+"\"}");
        string payload=Base64Url("{\"sub\":\""+EscapeJson(s.WeatherProjectId)+"\",\"iat\":"+iat.ToString(CultureInfo.InvariantCulture)+",\"exp\":"+exp.ToString(CultureInfo.InvariantCulture)+"}");
        string signing=header+"."+payload;
        byte[] data=Encoding.UTF8.GetBytes(signing);
        ISigner signer=SignerUtilities.GetSigner("Ed25519");
        signer.Init(true,parameters);
        signer.BlockUpdate(data,0,data.Length);
        byte[] signature=signer.GenerateSignature();
        jwtToken=signing+"."+Base64Url(signature);
        jwtExpires=DateTimeOffset.UtcNow.AddMinutes(29);
        return jwtToken;
    }
    internal static (string PrivateKey,string PublicKey) GenerateKeyPair()
    {
        var generator=new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        AsymmetricCipherKeyPair pair=generator.GenerateKeyPair();
        return (PrivatePem(Ed25519SeedBytes((Ed25519PrivateKeyParameters)pair.Private)),PublicPem(Ed25519PublicBytes((Ed25519PublicKeyParameters)pair.Public)));
    }
    // GetEncoded() semantics differ between BouncyCastle versions (raw 32 bytes, SPKI or PKCS#8);
    // normalize every known shape to the bare 32-byte seed / public key.
    internal static byte[] Ed25519SeedBytes(Ed25519PrivateKeyParameters key)
    {
        byte[] encoded=key.GetEncoded();
        if(encoded.Length==32)return encoded;
        if(encoded.Length==48 && encoded[0]==0x30 && encoded[14]==0x04 && encoded[15]==0x20)return encoded.Skip(16).Take(32).ToArray();
        throw new InvalidOperationException(Localization.Text("QWeather private key is invalid."));
    }
    internal static byte[] Ed25519PublicBytes(Ed25519PublicKeyParameters key)
    {
        byte[] encoded=key.GetEncoded();
        if(encoded.Length==32)return encoded;
        if(encoded.Length==44 && encoded[0]==0x30 && encoded[12]==0x03)return encoded.Skip(14).Take(32).ToArray();
        if(encoded.Length==48 && encoded[0]==0x30 && encoded[14]==0x04)return encoded.Skip(16).Take(32).ToArray();
        throw new InvalidOperationException(Localization.Text("QWeather private key is invalid."));
    }
    // Ed25519 PKCS#8: SEQUENCE { INTEGER 0, OID 1.3.101.112, OCTET STRING { OCTET STRING seed } }.
    private static string PrivatePem(byte[] seed)
    {
        byte[] der=new byte[48];der[0]=0x30;der[1]=0x2E;der[2]=0x02;der[3]=0x01;der[4]=0x00;
        der[5]=0x30;der[6]=0x05;der[7]=0x06;der[8]=0x03;der[9]=0x2B;der[10]=0x65;der[11]=0x70;
        der[12]=0x04;der[13]=0x22;der[14]=0x04;der[15]=0x20;
        Array.Copy(seed,0,der,16,32);
        return "-----BEGIN PRIVATE KEY-----\n"+PemBody(der)+"\n-----END PRIVATE KEY-----";
    }
    // Ed25519 SPKI: SEQUENCE { OID 1.3.101.112, BIT STRING publicKey }.
    private static string PublicPem(byte[] publicKey)
    {
        byte[] der=new byte[44];der[0]=0x30;der[1]=0x2A;der[2]=0x30;der[3]=0x05;der[4]=0x06;der[5]=0x03;
        der[6]=0x2B;der[7]=0x65;der[8]=0x70;der[9]=0x03;der[10]=0x21;der[11]=0x00;
        Array.Copy(publicKey,0,der,12,32);
        return "-----BEGIN PUBLIC KEY-----\n"+PemBody(der)+"\n-----END PUBLIC KEY-----";
    }
    private static string PemBody(byte[] der)
    {
        string base64=Convert.ToBase64String(der);
        var lines=new StringBuilder();
        for(int i=0;i<base64.Length;i+=64)lines.AppendLine(base64.Substring(i,Math.Min(64,base64.Length-i)));
        return lines.ToString().TrimEnd('\r','\n');
    }
    private static string Base64Url(string text)=>Base64Url(Encoding.UTF8.GetBytes(text));
    private static string Base64Url(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static string EscapeJson(string value)=>value.Replace("\\","\\\\").Replace("\"","\\\"");
    private static async Task<JsonDocument> Fetch(string url,CancellationToken token)
    {
        using var response=await client.GetAsync(url,token);response.EnsureSuccessStatusCode();return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }
    private static double Number(JsonElement value,string name)=>value.TryGetProperty(name,out var field) && field.ValueKind==JsonValueKind.Number && field.TryGetDouble(out var n) && double.IsFinite(n)?n:0;
    // QWeather returns numbers as JSON strings ("10"); accept both forms.
    private static double Num(JsonElement value,string name)
    {
        if(!value.TryGetProperty(name,out var field))return 0;
        return field.ValueKind switch
        {
            JsonValueKind.Number=>field.TryGetDouble(out var n)&&double.IsFinite(n)?n:0,
            JsonValueKind.String=>double.TryParse(field.GetString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var s)&&double.IsFinite(s)?s:0,
            _=>0
        };
    }
    private static double At(JsonElement value,string name,int index)=>value.TryGetProperty(name,out var field) && field.ValueKind==JsonValueKind.Array && index<field.GetArrayLength() && field[index].ValueKind==JsonValueKind.Number && field[index].TryGetDouble(out var n) && double.IsFinite(n)?n:0;
    private static string Text(JsonElement value,string name)=>value.TryGetProperty(name,out var field) && field.ValueKind==JsonValueKind.String?field.GetString()??"":"";
    internal static string Condition(int code)=>code switch{0=>"Clear skies",1=>"Mostly clear",2=>"Partly cloudy",3=>"Cloudy",45 or 48=>"Foggy",>=51 and <=57=>"Drizzle",>=61 and <=67=>"Rain",>=71 and <=77=>"Snow",>=80 and <=82=>"Rain showers",85 or 86=>"Snow showers",>=95=>"Thunderstorms",_=>"Weather"};
    internal static string ConditionText(WeatherSnapshot data)=>data.ConditionText.Length>0?data.ConditionText:Condition(data.Code);
    public void Dispose(){shutdown.Cancel();shutdown.Dispose();}
}
