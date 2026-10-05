using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using QuotaWidget.Core;

static class LocalizationTests
{
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        void Test(string name,Action run)=>tests.Add(("language: "+name,()=>
        {try{run();return Task.CompletedTask;}finally{Loc.Configure("zh-CN");}}));
        void Check(bool yes,string message){if(!yes)throw new Exception(message);}
        Test("system default, explicit override and persisted settings",()=>
        {
            Check(Loc.Resolve("auto",CultureInfo.GetCultureInfo("zh-TW"))=="zh-CN","Chinese OS not recognized");
            Check(Loc.Resolve("auto",CultureInfo.GetCultureInfo("de-DE"))=="en","non-Chinese OS fallback wrong");
            Check(Loc.Resolve("en",CultureInfo.GetCultureInfo("zh-CN"))=="en","explicit English ignored");
            Check(Loc.Resolve("zh-CN",CultureInfo.GetCultureInfo("en-US"))=="zh-CN","explicit Chinese ignored");
            var path=Path.Combine(Path.GetTempPath(),"qw-language-"+Guid.NewGuid()+".json");
            try
            {
                File.WriteAllText(path,"{\"monitoring\":\"codex\",\"compactMode\":true}");
                var old=WidgetSettings.Load(path,out _);
                Check(old.Language=="auto"&&old.Monitoring=="codex"&&old.CompactMode,"old settings migration changed preferences");
                old.Language="en";old.Save(path);
                Check(WidgetSettings.Load(path,out _).Language=="en","language not persisted");
                old.Language="invalid";old.Normalize();Check(old.Language=="auto","invalid language not normalized");
            }
            finally{File.Delete(path);}
        });
        Test("catalog formats retain all arguments and contain no untranslated entries",()=>
        {
            using var stream=typeof(Loc).Assembly.GetManifestResourceStream("QuotaWidget.Core.Strings.en.json")!;
            var catalog=JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!;
            Check(catalog.Count>350,"catalog is incomplete");
            string Args(string value)=>string.Join(",",Regex.Matches(value,@"(?<!\{)\{(\d+)(?:[,:][^{}]*)?\}").Select(m=>m.Groups[1].Value).Order());
            foreach(var (source,english) in catalog)
            {
                Check(!string.IsNullOrWhiteSpace(english)&&!english.Any(c=>c is >= '\u4e00' and <= '\u9fff'),"untranslated entry: "+source);
                Check(Args(source)==Args(english),"format argument mismatch: "+source);
                _=CompositeFormat.Parse(source);_=CompositeFormat.Parse(english);
            }
            var root=new DirectoryInfo(AppContext.BaseDirectory);
            while(root is not null&&!File.Exists(Path.Combine(root.FullName,"src/QuotaWidget/MainWindow.xaml")))root=root.Parent;
            Check(root is not null,"source tree not found for XAML resource validation");
            var xaml=XDocument.Load(Path.Combine(root!.FullName,"src/QuotaWidget/MainWindow.xaml"));
            foreach(var attribute in xaml.Descendants().Attributes().Where(a=>a.Name.LocalName.StartsWith("Translate.")))
                Check(catalog.ContainsKey(attribute.Value),"XAML has no translation: "+attribute.Value);
            Loc.Configure("en");
            Check(Loc.F($"{12.5:0.#} 分钟")=="12.5 min","numeric format changed");
            Check(Loc.T("User-owned project title")=="User-owned project title","unknown text changed");
        });
        Test("quota, gaps, recent rates and smoothed curves do not depend on language",()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"qw-language-data-"+Guid.NewGuid());
            var now=new DateTimeOffset(2026,10,5,12,0,0,TimeSpan.Zero);
            try
            {
                var paths=new DataPaths(Path.Combine(root,"demo"));DemoData.Generate(paths,now,"normal");
                var model=new WidgetModel(paths,true){ReadOnly=true};model.Initialize();
                model.Settings.RangeMinutes=300;
                string Snapshot(WidgetView v)=>JsonSerializer.Serialize(new
                {
                    v.SummaryTotal,v.SummaryFable,v.SummaryCoverage,v.Five.UsedPercent,
                    Week=v.Week.UsedPercent,v.Chart.Gaps,v.Chart.Total.Segments,
                    Trend=v.Chart.TotalTrend.Runs,Recent=RecentUsageRate.Build(v.Chart.Total,now,300)
                });
                Loc.Configure("zh-CN");var chinese=model.BuildView(now);var baseline=Snapshot(chinese);
                Loc.Configure("en");var english=model.BuildView(now);
                Check(Snapshot(english)==baseline,"language changed numerical or structural data");
                Check(chinese.DisplayLabel!=english.DisplayLabel&&english.DisplayLabel=="Remaining","view did not translate");
                Check(!english.SummaryDetail.Any(c=>c is >= '\u4e00' and <= '\u9fff'),"Chinese diagnostic in English view");
                Loc.Configure("zh-CN");Check(Snapshot(model.BuildView(now))==baseline,"round trip changed data");
            }
            finally{Directory.Delete(root,true);}
        });
    }
}
