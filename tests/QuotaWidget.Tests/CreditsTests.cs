using System.Text.Json;
using QuotaWidget.Core;

static class CreditsTests
{
    static readonly DateTimeOffset At = new(2026,10,20,0,0,0,TimeSpan.Zero);
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    static HistoryRecord Record(int minute,decimal? balance,bool unlimited=false,string profile="fixture",double quota=100) =>
        new("codex-usage",profile,"Codex pro",At.AddMinutes(minute),Statuses.Partial,300,
            new("credit-"+minute,At.AddMinutes(minute),new(null,UsageParser.Limit(quota,At.AddDays(7)),null),new(balance>0,unlimited,balance)));
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        void Test(string name,Action body)=>tests.Add(("Credits: "+name,()=>{body();return Task.CompletedTask;}));
        Test("official decimal balance, unlimited, null and malformed independently of quota",()=>
        {
            CreditBalance? Parse(string text) { using var d=JsonDocument.Parse(text); return CreditsUsage.Parse(d.RootElement); }
            Check(Parse("""{"hasCredits":true,"unlimited":false,"balance":"1234.1234567890"}""")?.Balance==1234.1234567890m,"precision lost");
            Check(Parse("""{"hasCredits":false,"unlimited":false,"balance":0}""")?.Balance==0,"zero lost");
            Check(Parse("""{"hasCredits":false,"unlimited":true,"balance":null}""") is {Unlimited:true,Balance:null},"unlimited lost");
            Check(Parse("""{"hasCredits":true,"unlimited":false}""")?.Balance is null,"absent balance fabricated");
            foreach(var value in new[]{"-1","\"NaN\"","\"1,000\"","true","{}","1e100"})
                Check(Parse("{\"hasCredits\":true,\"unlimited\":false,\"balance\":"+value+"}") is null,"invalid balance accepted: "+value);
            string Response(string credit,string windows="null")=>"{\"accountId\":\"fixture\",\"rateLimitsByLimitId\":{\"codex\":{\"planType\":\"pro\",\"primary\":"+windows+",\"credits\":"+credit+"}}}";
            LatestEnvelope Fetch(string body) { using var d=JsonDocument.Parse(body); return CodexUsageSource.Parse(d.RootElement,At); }
            var zero=Fetch(Response("""{"hasCredits":false,"unlimited":false,"balance":"0"}"""));
            Check(zero.Snapshot?.Credits?.Balance==0&&zero.Snapshot.Limits.AllWeek is null,"credits-only rejected");
            Check(SnapshotJson.ParseEnvelope(SnapshotJson.WriteEnvelope(zero),[]) is not null,"credits-only storage rejected");
            var malformed=Fetch(Response("{\"hasCredits\":true}","{\"usedPercent\":100,\"windowDurationMins\":10080,\"resetsAt\":1793000000}"));
            Check(malformed.Snapshot is {Credits:null,Limits.AllWeek.UsedPercent:100},"bad credits erased quota");
        });
        Test("history roundtrip, exact decimal dedupe, and old snapshots remain compatible",()=>
        {
            var r=Record(0,1234.123456789m);
            var parsed=HistoryStore.ParseLine(HistoryStore.FormatLine(r));
            Check(parsed?.Snapshot==r.Snapshot,"history credits lost");
            Check(SnapshotJson.Canonical(r.Snapshot)!=SnapshotJson.Canonical(r.Snapshot with{Credits=new(true,false,1234.123456788m)}),"different balances deduped");
            var old=r with{Snapshot=r.Snapshot with{Credits=null}};
            Check(HistoryStore.ParseLine(HistoryStore.FormatLine(old))?.Snapshot.Credits is null,"legacy history became zero");
            var invalid=HistoryStore.FormatLine(r).Replace("\"balance\":1234.123456789","\"secret\":1,\"balance\":1234.123456789");
            Check(HistoryStore.ParseLine(invalid) is null,"unrecognized stored fields accepted");
        });
        Test("net decrease and quota reset are independent, no 100 point cap or smoothing",()=>
        {
            var records=new[]{Record(0,1000.00001m),Record(5,800m),Record(10,750m,quota:0),Record(15,750m,quota:1)};
            var data=CreditsUsage.Build(records,[]);
            Check(data.Segments.All(s=>s.Valid),"quota reset broke credits");
            Check(Math.Abs(RateEngine.SumRange(data,At,At.AddMinutes(15)).Delta-250.00001)<1e-8,"net decreases incorrect");
            Check(data.Segments[0].Rate>2400,"credits capped at quota 100");
            Check(data.Segments[2].Delta==0,"flat balance not observed zero");
            Check(RateEngine.SumRange(data,At.AddMinutes(2),At.AddMinutes(15)).Delta==50,"partial interval invented");
            Check(CumulativeSeries.Build(data,At,At.AddMinutes(15)).Segments.Last().To==250.00001,"cumulative not conserved");
        });
        Test("topups, gaps, profile changes and unavailable balances never become spending",()=>
        {
            var records=new[]{Record(0,500),Record(5,490),Record(10,1000),Record(15,990),Record(20,null),Record(25,980),Record(30,970,true),Record(35,960),Record(55,900),Record(60,890,profile:"other")};
            var data=CreditsUsage.Build(records,[]);
            Check(RateEngine.SumRange(data,At,At.AddHours(1)).Delta==20,"uncertain or topup interval counted");
            Check(data.Segments[1] is {Issue:SegmentIssue.Reset,Label:"余额增加"},"increase not separated");
            Check(data.Segments[0].Group!=data.Segments[2].Group,"topup bridged");
            var failed=CreditsUsage.Build([Record(0,500),Record(5,450)],[new(At.AddMinutes(2),EventTypes.CollectFail)]);
            Check(!failed.Segments[0].Valid,"collection failure bridged");
            var absent=CreditsUsage.Build([Record(0,500) with{Snapshot=Record(0,500).Snapshot with{Credits=null}},Record(5,450)],[]);
            Check(!absent.Segments[0].Valid,"upgrade fabricated history");
        });
        Test("model reload and all range retain credit history independently",()=>
        {
            var dir=Path.Combine(Path.GetTempPath(),"qw-credit-test-"+Guid.NewGuid().ToString("N"));
            try
            {
                var paths=new DataPaths(dir);var store=new HistoryStore(paths,false);
                foreach(var r in new[]{Record(0,100),Record(5,90),Record(10,85)}) store.Append(r,At.AddMinutes(15));
                var last=Record(10,85);
                AtomicFile.WriteAllText(paths.Latest,SnapshotJson.WriteEnvelope(new(1,last.SourceId,last.ProfileKey,last.PlanLabel,last.T,last.Status,300,null,null,last.Snapshot)));
                var model=new WidgetModel(paths,false,()=>At.AddMinutes(15));model.Initialize();
                foreach(var range in new[]{60,ChartRanges.All,60})
                {
                    var view=model.BuildView(At.AddMinutes(15),range);
                    Check(view.Chart.CreditsBalance?.Balance==85,"balance lost on reload");
                    Check(RateEngine.SumRange(view.Chart.Credits!,At,At.AddMinutes(15)).Delta==15,"range/reload lost net amount");
                }
            }
            finally { if(Directory.Exists(dir)) Directory.Delete(dir,true); }
        });
        Test("ongoing credit-funded work cannot pull new weekly usage before an exhausted reset",()=>
        {
            HistoryRecord NewPeriod(int minute,decimal balance,double used)
            {
                var r=Record(minute,balance,quota:used);
                return r with{Snapshot=r.Snapshot with{Limits=new(null,UsageParser.Limit(used,At.AddDays(14)),null)}};
            }
            var records=new[]{Record(-10,1000,quota:99),Record(-5,1000),Record(0,950),Record(5,900),NewPeriod(10,880,0),NewPeriod(15,880,1),NewPeriod(20,880,2)};
            foreach(var withCredits in new[]{true,false})
            foreach(var withWork in new[]{true,false})
            {
                var selected=withCredits?records:records.Select(r=>r with{Snapshot=r.Snapshot with{Credits=null}}).ToArray();
                var quota=RateEngine.Build(selected,SeriesKey.Total,[]);
                var credits=CreditsUsage.Build(selected,[]);
                var work=withWork?new[]{new WorkSpan(At.AddMinutes(-10),At.AddMinutes(30),true,false,null)}:[];
                foreach(var end in new[]{5,10,15,20})
                {
                    var trend=ActiveRateEstimator.Build(quota,At.AddMinutes(end),work);
                    Check(trend.ValueAt(At.AddMinutes(-2))==0&&trend.ValueAt(At.AddMinutes(2))==0,"weekly rate appeared while capped");
                    if(end>=10)Check(trend.ValueAt(At.AddMinutes(7)) is null,"unknown reset interval was smoothed");
                    if(end>=15)Check(trend.ValueAt(At.AddMinutes(12))>0,"post-reset quota disappeared");
                    var measured=quota.Segments.Where(s=>s.Valid&&s.End<=At.AddMinutes(end)).Sum(s=>s.Delta);
                    Check(Math.Abs(trend.Delta-measured)<1e-8,"quota accounting changed");
                }
                foreach(var range in new[]{60,300,1440,4320})
                {
                    var view=new ChartView{Start=At.AddMinutes(20-range),End=At.AddMinutes(20),Total=quota,Fable=quota,Codex=quota,Credits=credits,Gaps=[],Smooth=true,Activity=new([],[],work)};
                    Check(view.CodexAt(At.AddMinutes(2))==0&&view.CodexAt(At.AddMinutes(7)) is null&&view.CodexAt(At.AddMinutes(12))>0,"view or hover crossed funding boundary");
                }
                if(withCredits)Check(RateEngine.SumRange(credits,At.AddMinutes(-10),At.AddMinutes(20)).Delta==120&&credits.ValueAt(At.AddMinutes(7))>0&&credits.ValueAt(At.AddMinutes(12))==0,"credit observations were forced to match quota");
                var interrupted=RateEngine.Build(selected,SeriesKey.Total,[new(At.AddMinutes(2),EventTypes.CollectFail,Statuses.Error,"network")]);
                Check(ActiveRateEstimator.Build(interrupted,At.AddMinutes(20),work).ValueAt(At.AddMinutes(2)) is null,"capped gap became observed zero");
            }
        });
    }
}
