using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace NabyScape;
internal sealed class WeatherView : UserControl
{
    private readonly App app;
    private Viewbox? fit;
    private double fitBudget=double.PositiveInfinity;
    internal void FitTo(double width,double height)
    {
        fitBudget=Math.Max(120,height);page.Width=width;fit??=new Viewbox{Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly};
        fit.MaxHeight=location.Visibility==Visibility.Visible?double.PositiveInfinity:fitBudget;
        if(!ReferenceEquals(Content,fit)){Content=null;fit.Child=page;Content=fit;}
    }
    internal event Action? ContentSizeChanged;
    private readonly StackPanel page=new(),content=new(),location=new(),results=new();
    private readonly TextBlock city=UI.Text("",11,UI.Muted),status=UI.Text("",10,UI.Muted);
    private readonly TextBox search=UI.Input("Weather city search", "", "Search for your city");
    private readonly Button find,retry,credit;
    private CancellationTokenSource? lifetime,searchRequest;
    private string shownKey="";
    private bool busy;
    private int revision;
    private DateTimeOffset checkedAt;
    private WeatherSnapshot? shown;
    internal WeatherView(App app,bool configure=false)
    {
        this.app=app;
        var header=new DockPanel{Margin=new(0,0,0,6)};
        var choose=Icons.Button("settings","Choose weather city and units",()=>location.Visibility=location.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible,false,28);choose.Background=Brushes.Transparent;choose.Margin=new(0);DockPanel.SetDock(choose,Dock.Right);header.Children.Add(choose);
        var title=new StackPanel();title.Children.Add(UI.Heading(configure?"Location & forecast":"Weather",16));city.Margin=new(0,1,0,0);city.TextTrimming=TextTrimming.CharacterEllipsis;city.TextWrapping=TextWrapping.NoWrap;title.Children.Add(city);header.Children.Add(title);page.Children.Add(header);
        var searchRow=new DockPanel();find=UI.Button("Search",()=>_=Search());find.Padding=new(12,6,12,6);find.Margin=new(8,0,0,0);DockPanel.SetDock(find,Dock.Right);searchRow.Children.Add(find);search.Margin=new(0);search.MaxLength=120;var searchHost=new Grid();searchHost.Children.Add(search);var searchHint=UI.Text("Search city or town…",12,UI.Muted);searchHint.Margin=new(11,0,0,0);searchHint.IsHitTestVisible=false;searchHost.Children.Add(searchHint);search.TextChanged+=(_,_)=>searchHint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;searchRow.Children.Add(searchHost);location.Children.Add(searchRow);
        search.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){_=Search();e.Handled=true;}};
        var options=new ComboBox{ItemsSource=new[]{"Celsius","Fahrenheit"},SelectedItem=app.Settings.WeatherUnits,MinHeight=30,Margin=new(0,8,0,8)};UI.Named(options,"Weather temperature units");options.SelectionChanged+=(_,_)=>{if(options.SelectedItem is string unit && app.Settings.WeatherUnits!=unit){app.Settings.WeatherUnits=unit;app.ChangeSettings();}};location.Children.Add(options);
        var sources=new ComboBox{ItemsSource=new[]{Localization.Text("NMC · China weather"),Localization.Text("QWeather · China hourly"),Localization.Text("Open-Meteo · International")},SelectedIndex=app.Settings.WeatherSource switch{"nmc"=>0,"qweather"=>1,_=>2},MinHeight=30,Margin=new(0,0,0,2)};UI.Named(sources,"Weather source");sources.SelectionChanged+=(_,_)=>{string code=sources.SelectedIndex switch{0=>"nmc",1=>"qweather",_=>"open-meteo"};if(app.Settings.WeatherSource!=code){app.Settings.WeatherSource=code;app.ChangeSettings();}};location.Children.Add(sources);
        var keyInput=UI.Input("QWeather API key",app.Settings.WeatherApiKey,"");keyInput.Margin=new(0,2,0,8);UI.Named(keyInput,"QWeather API key");
        void CommitKey(){string value=keyInput.Text.Trim();if(app.Settings.WeatherApiKey!=value){app.Settings.WeatherApiKey=value;app.ChangeSettings();}}
        keyInput.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){CommitKey();e.Handled=true;}};
        keyInput.LostFocus+=(_,_)=>CommitKey();
        keyInput.Visibility=app.Settings.WeatherSource=="qweather"?Visibility.Visible:Visibility.Collapsed;
        sources.SelectionChanged+=(_,_)=>keyInput.Visibility=sources.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;
        location.Children.Add(keyInput);
        var guide=UI.CaptionButton("How to get a QWeather key?",ShowQWeatherGuide);guide.HorizontalAlignment=HorizontalAlignment.Left;guide.Margin=new(0,0,0,6);
        guide.Visibility=keyInput.Visibility;
        sources.SelectionChanged+=(_,_)=>guide.Visibility=sources.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;
        location.Children.Add(guide);
        var sourceHint=UI.Text(Localization.Text("NMC works in mainland China without a key. QWeather needs a free key from qweather.com. Open-Meteo suits other regions."),11,UI.Muted);sourceHint.Margin=new(0,2,0,8);location.Children.Add(sourceHint);
        var list=UI.Scroll(results);list.Tag="panel-content-scroll";list.MaxHeight=150;location.Children.Add(list);location.Margin=new(0,0,0,8);page.Children.Add(location);page.Children.Add(content);
        var footer=new DockPanel{Margin=new(0,6,0,0)};retry=Icons.Button("reset","Refresh weather",()=>_=Reload(true),false,26);retry.Margin=new(0);retry.Background=Brushes.Transparent;DockPanel.SetDock(retry,Dock.Right);footer.Children.Add(retry);footer.Children.Add(status);page.Children.Add(footer);
        credit=UI.Button("Open-Meteo",()=>ShortcutService.Open(new(){Target=app.Settings.WeatherSource switch{"nmc"=>"https://www.nmc.cn/","qweather"=>"https://www.qweather.com/",_=>"https://open-meteo.com/"}}));credit.FontSize=9;credit.Foreground=UI.Muted;credit.Background=Brushes.Transparent;credit.BorderThickness=new(0);credit.Padding=new(4,0,4,0);credit.MinHeight=20;credit.Margin=new(0);DockPanel.SetDock(credit,Dock.Right);footer.Children.Insert(1,credit);UpdateCredit();
        location.IsVisibleChanged+=(_,_)=>{if(fit!=null)fit.MaxHeight=location.Visibility==Visibility.Visible?double.PositiveInfinity:fitBudget;};
        page.SizeChanged+=(_,_)=>ContentSizeChanged?.Invoke();
        Content=page;IsVisibleChanged+=(_,_)=>
        {
            if(!IsLoaded)return;
            if(IsVisible){lifetime??=new();_=Reload();}
            else{lifetime?.Cancel();lifetime?.Dispose();lifetime=null;searchRequest?.Cancel();searchRequest?.Dispose();searchRequest=null;revision++;busy=false;find.IsEnabled=true;}
        };Loaded+=LoadedView;Unloaded+=UnloadedView;location.Visibility=configure || WeatherService.Key(app.Settings).Length==0?Visibility.Visible:Visibility.Collapsed;
        city.Text=app.Settings.WeatherCity.Length>0?app.Settings.WeatherCity:"Your daily outlook";Render(null);
    }
    internal void OpenLocationEditor(){location.Visibility=Visibility.Visible;}
    internal void ShowQWeatherGuide()
    {
        var owner=Window.GetWindow(this);
        var window=new Window{Title=Localization.Text("QWeather key guide"),Width=500,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,
            WindowStartupLocation=owner==null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner,Background=UI.Brush("#16191F"),
            Icon=tryLoadIcon()};
        var panel=new StackPanel{Margin=new(18,16,18,14)};
        panel.Children.Add(UI.Heading(Localization.Text("Get a free QWeather key"),15));
        foreach(var step in new[]{
            Localization.Text("1. Open console.qweather.com and sign up for a free account."),
            Localization.Text("2. In the console, create a project, then add an API key credential to it."),
            Localization.Text("3. Copy the key and paste it into the box above."),
            Localization.Text("4. If your console shows a dedicated API Host, enter it as host|key, for example abc.re.qweatherapi.com|your-key.")})
        {
            var line=UI.Text(step,12);line.TextWrapping=TextWrapping.Wrap;line.Margin=new(0,8,0,0);panel.Children.Add(line);
        }
        var note=UI.Text(Localization.Text("The free plan covers current conditions, 24 hourly and 7 daily forecasts. Your key stays in local settings."),11,UI.Muted);
        note.TextWrapping=TextWrapping.Wrap;note.Margin=new(0,12,0,0);panel.Children.Add(note);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,16,0,0)};
        var docs=UI.Button("Authentication docs",()=>ShortcutService.Open(new(){Target="https://developers.qweather.com/docs/configuration/authentication/"}));docs.Margin=new(0,0,8,0);
        var console=UI.Button("Open QWeather console",()=>ShortcutService.Open(new(){Target="https://console.qweather.com/"}));console.Margin=new(0,0,8,0);
        var done=UI.Button("Done",()=>window.Close(),true);
        foreach(var button in new[]{docs,console,done})actions.Children.Add(button);
        panel.Children.Add(actions);window.Content=panel;
        if(owner!=null)window.ShowDialog();else window.Show();
    }
    private static System.Windows.Media.ImageSource? tryLoadIcon()
    {
        try
        {
            var uri=new Uri("pack://application:,,,/Assets/island.ico");
            return System.Windows.Media.Imaging.BitmapFrame.Create(uri);
        }
        catch(Exception){return null;}
    }
    private void LoadedView(object sender,RoutedEventArgs e)
    {
        lifetime?.Cancel();lifetime?.Dispose();lifetime=IsVisible?new():null;app.Updated+=Heartbeat;app.SettingsChanged+=Preferences;if(IsVisible)_=Reload();
    }
    private void UnloadedView(object sender,RoutedEventArgs e)
    {
        app.Updated-=Heartbeat;app.SettingsChanged-=Preferences;lifetime?.Cancel();lifetime?.Dispose();lifetime=null;searchRequest?.Cancel();searchRequest?.Dispose();searchRequest=null;revision++;busy=false;
    }
    private void Preferences()
    {
        foreach(var art in Artworks(page)){art.ReduceMotion=app.Settings.ReduceMotion;art.RefreshMotion();}
        UpdateCredit();
        if(IsVisible && shownKey!=WeatherService.Key(app.Settings))_=Reload();
    }
    private void UpdateCredit()
    {
        string label=app.Settings.WeatherSource switch{"nmc"=>"NMC","qweather"=>"QWeather",_=>"Open-Meteo"};
        if(credit.Content as string!=label){credit.Content=label;UI.Named(credit,label);}
    }
    private static System.Collections.Generic.IEnumerable<WeatherArtwork> Artworks(DependencyObject node)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()){if(child is WeatherArtwork art)yield return art;foreach(var nested in Artworks(child))yield return nested;}
    }
    private void Heartbeat()
    {
        if(!IsVisible || (app.CurrentUpdate&RuntimeChange.Clock)==0 || Window.GetWindow(this)?.WindowState==WindowState.Minimized || DateTimeOffset.UtcNow-checkedAt<TimeSpan.FromMinutes(1))return;
        checkedAt=DateTimeOffset.UtcNow;
        if(!busy && DateTimeOffset.UtcNow-app.Weather.LastAttempt>TimeSpan.FromMinutes(3) && (shown==null || DateTimeOffset.UtcNow-shown.Updated>TimeSpan.FromMinutes(20)))_=Reload();
    }
    private async Task Search()
    {
        if(lifetime==null || search.Text.Trim().Length<2){status.Text="Enter at least two letters of your city.";return;}
        if(app.Settings.WeatherSource=="qweather" && app.Settings.WeatherApiKey.Length==0){status.Text=Localization.Text("Enter your QWeather API key first.");return;}
        searchRequest?.Cancel();searchRequest?.Dispose();searchRequest=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var request=searchRequest;find.IsEnabled=false;results.Children.Clear();status.Text="Looking for cities…";
        try
        {
            var places=await app.Weather.Search(app.Settings,search.Text,request.Token);if(!IsLoaded || request!=searchRequest)return;
            foreach(var place in places)
            {
                var button=UI.Button(place.Name,()=>{app.Settings.WeatherCity=place.Name;app.Settings.WeatherLatitude=place.Latitude;app.Settings.WeatherLongitude=place.Longitude;app.Settings.WeatherStationCode=place.StationCode;location.Visibility=Visibility.Collapsed;app.ChangeSettings();});button.FontSize=11;button.Margin=new(0,2,0,2);button.HorizontalContentAlignment=HorizontalAlignment.Left;button.Content=new TextBlock{Text=place.Name,TextTrimming=TextTrimming.CharacterEllipsis,Foreground=Brushes.White};results.Children.Add(button);
            }
            status.Text=places.Length==0?(app.Settings.WeatherSource=="nmc"?"No stations found. Try a nearby city or its province.":"No cities found. Try a nearby city or include the country."):"Choose your city above.";
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(IsLoaded)status.Text=ex is InvalidOperationException?ex.Message:"City search is unavailable. Please try again.";}
        finally{if(IsLoaded && request==searchRequest)find.IsEnabled=true;}
    }
    private async Task Reload(bool force=false)
    {
        if(lifetime==null)return;string key=WeatherService.Key(app.Settings);int version=++revision;shownKey=key;city.Text=app.Settings.WeatherCity.Length>0?app.Settings.WeatherCity:"Your daily outlook";
        if(key.Length==0){shown=null;busy=false;Render(null);status.Text="Choose a city to see today's weather.";retry.IsEnabled=false;return;}
        if(shown?.Key!=key){shown=app.Weather.Cached?.Key==key?app.Weather.Cached:null;Render(shown);}
        busy=true;retry.IsEnabled=false;status.Text="Updating weather…";
        try
        {
            var snapshot=await app.Weather.Get(app.Settings,force,lifetime.Token);if(!IsLoaded || version!=revision)return;
            shown=snapshot;Render(snapshot);status.Text="Updated "+snapshot.Updated.ToLocalTime().ToString("HH:mm");
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(IsLoaded && version==revision)status.Text=shown==null?(ex is InvalidOperationException?ex.Message:"Weather unavailable. Refresh to try again."):"Saved outlook · connection unavailable";}
        finally{if(IsLoaded && version==revision){busy=false;retry.IsEnabled=true;}}
    }
    private void Render(WeatherSnapshot? data)
    {
        content.Children.Clear();bool day=data?.Day??true;int code=data?.Code??0;
        if(data==null)
        {var hint=UI.Text("Your daily outlook starts with a city. Search above to get started.",11,UI.Muted);hint.Margin=new(0,8,0,8);content.Children.Add(hint);ContentSizeChanged?.Invoke();return;}
        var scene=new Grid();scene.Children.Add(new WeatherSky{Day=day,Code=code});var panel=new StackPanel{Margin=new(14,8,12,8)};
        var hero=new Grid();hero.ColumnDefinitions.Add(new());hero.ColumnDefinitions.Add(new(){Width=new GridLength(108)});
        var readings=new StackPanel();var today=UI.Text(day?"TODAY · DAYLIGHT":"TONIGHT",9,UI.Brush("#B6C7D9"));readings.Children.Add(today);
        var temperature=UI.Text($"{data.Temperature:0}°",38);temperature.FontWeight=FontWeights.Light;temperature.TextWrapping=TextWrapping.NoWrap;readings.Children.Add(temperature);
        readings.Children.Add(UI.Text(WeatherService.ConditionText(data),12));var range=UI.Text($"Feels {data.FeelsLike:0}°  ·  H {data.High:0}°  L {data.Low:0}°",10,UI.Brush("#B7CADC"));range.Margin=new(0,4,0,0);readings.Children.Add(range);hero.Children.Add(readings);
        var art=new WeatherArtwork{Code=code,Day=day,Width=92,Height=76,ReduceMotion=app.Settings.ReduceMotion};Grid.SetColumn(art,1);hero.Children.Add(art);panel.Children.Add(hero);
        var metrics=new System.Windows.Controls.Primitives.UniformGrid{Columns=3,Rows=1,Margin=new(0,7,0,0)};
        void Metric(string label,string value){var p=new StackPanel{Orientation=Orientation.Horizontal};p.Children.Add(UI.Text(label+" ",9,UI.Brush("#97ADC2")));p.Children.Add(UI.Text(value,10,UI.Brush("#DFE8EF")));metrics.Children.Add(p);}
        Metric("Rain",$"{data.RainChance:0}%");Metric("Humidity",$"{data.Humidity:0}%");Metric("Wind",$"{data.Wind:0} km/h");panel.Children.Add(metrics);scene.Children.Add(panel);
        content.Children.Add(new Border{CornerRadius=new(16),BorderBrush=UI.Brush(day?"#3C5268":"#354364"),BorderThickness=new(.7),Child=scene});
        var forecast=new Grid{Margin=new(10,5,10,4)};forecast.RowDefinitions.Add(new());forecast.RowDefinitions.Add(new(){Height=new GridLength(10)});var hourly=new System.Windows.Controls.Primitives.UniformGrid{Columns=Math.Max(1,data.Hours.Count),Rows=1};
        foreach(var hour in data.Hours)
        {
            var column=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center};column.Children.Add(UI.Text(hour.Time,9,UI.Muted));column.Children.Add(new WeatherArtwork{Code=hour.Code,Day=hour.Day,Width=22,Height=18,Margin=new(0,3,0,1),ReduceMotion=true,DecorativeMotion=false});var value=UI.Text($"{hour.Temperature:0}°",11);value.HorizontalAlignment=HorizontalAlignment.Center;column.Children.Add(value);column.ToolTip=$"{WeatherService.Condition(hour.Code)} · {hour.RainChance:0}% rain";hourly.Children.Add(column);
        }
        forecast.Children.Add(hourly);var trend=new WeatherTrend{Height=10};Grid.SetRow(trend,1);forecast.Children.Add(trend);
        string Clock(string value)=>value.Length>=16?value.Substring(11,5):"—";
        content.Children.Add(new Border{Child=forecast,Background=UI.Brush("#111820"),BorderBrush=UI.Brush("#273440"),BorderThickness=new(.6),CornerRadius=new(12),Margin=new(0,6,0,0),ToolTip="Next hours · Sunrise "+Clock(data.Sunrise)+" · Sunset "+Clock(data.Sunset)});
        ContentSizeChanged?.Invoke();
    }
}
