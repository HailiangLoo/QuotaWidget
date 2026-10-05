using QuotaWidget.Core;

static class CalendarTests
{
    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        void Test(string name, Action body) => tests.Add(("calendar: " + name, () => { body(); return Task.CompletedTask; }));
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        void Near(double expected, double? actual) => Check(actual is { } n && Math.Abs(n-expected)<1e-8, $"expected {expected}, got {actual}");
        var zone=TimeZoneInfo.FindSystemTimeZoneById("Singapore Standard Time");
        var day=new DateTime(2026,9,30);
        var midnight=DailyQuota.Midnight(day.AddDays(1),zone);
        RateSegment Segment(DateTimeOffset a,DateTimeOffset b,double delta,SegmentIssue issue=SegmentIssue.None) => new() {Start=a,End=b,Delta=delta,Issue=issue};
        SeriesData Series(params RateSegment[] segments) => new() {Key=SeriesKey.Total,Segments=segments.ToList()};

        Test("midnight and month boundaries conserve observed consumption", () =>
        {
            var data=Series(Segment(midnight.AddMinutes(-15),midnight.AddMinutes(5),2.4));
            var both=DailyQuota.Build(data,day,day.AddDays(2),zone,midnight.AddDays(1));
            Near(1.8,both[0].Points); Near(.6,both[1].Points);
            Near(2.4,both.Sum(d=>d.Points??0));
            Check(both.All(d=>d.SplitAtMidnight),"estimated midnight allocation not marked");
            Near(15,both[0].CoverageMinutes); Near(5,both[1].CoverageMinutes);
            Near(both[0].Points!.Value,DailyQuota.Build(data,day,day.AddDays(1),zone,midnight.AddDays(1)).Single().Points);
            Near(both[1].Points!.Value,DailyQuota.Build(data,day.AddDays(1),day.AddDays(2),zone,midnight.AddDays(1)).Single().Points);
        });
        Test("observed zero differs from missing, invalid or future coverage", () =>
        {
            var start=midnight.AddDays(-1);
            var data=Series(Segment(start,start.AddMinutes(5),0),
                Segment(start.AddMinutes(5),start.AddMinutes(10),50,SegmentIssue.Reset),
                Segment(start.AddDays(1),start.AddDays(1).AddMinutes(5),8,SegmentIssue.Gap),
                Segment(start.AddDays(2),start.AddDays(2).AddMinutes(5),-3),
                Segment(start.AddDays(3),start.AddDays(3).AddMinutes(5),double.NaN),
                Segment(start.AddDays(4),start.AddDays(4).AddMinutes(5),3));
            var days=DailyQuota.Build(data,day,day.AddDays(6),zone,start.AddDays(4));
            Near(0,days[0].Points); Near(5,days[0].CoverageMinutes);
            Check(days[0].HasGap && days[1].HasGap && days[2].HasGap && days[3].HasGap,"invalid intervals not marked");
            Check(days.Skip(1).All(d=>d.Points is null),"missing, invalid or future interval became a number");
        });
        Test("local days respect both 23 and 25 hour DST boundaries", () =>
        {
            var eastern=TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            foreach(var (d,hours) in new[] {(new DateTime(2026,3,8),23),(new DateTime(2026,11,1),25)})
            {
                var a=DailyQuota.Midnight(d,eastern); var b=DailyQuota.Midnight(d.AddDays(1),eastern);
                Near(hours,(b-a).TotalHours);
                var result=DailyQuota.Build(Series(Segment(a,b,7.5)),d,d.AddDays(1),eastern,b).Single();
                Near(7.5,result.Points); Near(hours*60,result.CoverageMinutes);
                Check(!result.SplitAtMidnight,"whole local day treated as a clipped interval");
            }
        });
        Test("old month is bounded, profile isolated and excludes Fable double counting", () => WithRoot(paths =>
        {
            var edge=DailyQuota.Midnight(new DateTime(2026,10,1),TimeZoneInfo.Local);
            var now=edge.AddDays(14).AddHours(12);
            new WidgetSettings {LastSourceId="test-usage",LastProfileKey="p"}.Save(paths.Settings);
            Write(paths,Record(edge.AddMinutes(-2.5),10)); Write(paths,Record(edge.AddMinutes(2.5),12));
            Write(paths,Record(now.AddMinutes(-5),29)); Write(paths,Record(now,30));
            Write(paths,Record(edge.AddMinutes(2),99) with {ProfileKey="wrong"},"p");
            var dir=new HistoryStore(paths,false).ProfileDir("test-usage","p");
            File.WriteAllText(Path.Combine(dir,"2026-08-01.jsonl"),"unreadable old record");
            File.WriteAllText(Path.Combine(dir,"2026-11-01.jsonl"),"unreadable future record");
            var model=new WidgetModel(paths,false,()=>now); model.Initialize();
            var skipped=model.Store.SkippedLines; var index=model.Store.IndexCount;
            var september=model.CalendarMonth(new DateTime(2026,9,1),now);
            Near(1,september.Single(d=>d.Day.Day==30).Points); Near(1,september.Sum(d=>d.Points??0));
            Check(model.Store.IndexCount==index && model.Records.Count==2,"monthly read grew live retention");
            Check(model.Store.SkippedLines==skipped+1,"out-of-range file parsed or wrong profile accepted");
            Check(ReferenceEquals(september,model.CalendarMonth(new DateTime(2026,9,22),now)),"unchanged month not cached");
            Near(1,model.CalendarMonth(new DateTime(2026,10,1),now).Single(d=>d.Day.Day==1).Points);
            model.ReleaseCalendar();
            var eventLog=new EventLog(paths);
            eventLog.Append(new AppEvent(edge,EventTypes.Suspend));
            var interrupted=model.CalendarMonth(new DateTime(2026,9,1),now).Last();
            Check(interrupted.Points is null && interrupted.HasGap,"known outage counted as consumption");
        }));
        Test("bounded history and event reads enforce exact timestamp limits", () => WithRoot(paths =>
        {
            var clock=DateTimeOffset.Now.AddMinutes(-30);
            var start=new DateTimeOffset(clock.Ticks-clock.Ticks%TimeSpan.TicksPerMillisecond,clock.Offset); var end=start.AddMinutes(10);
            var store=new HistoryStore(paths,false); var events=new EventLog(paths);
            foreach(var t in new[] {start.AddSeconds(-1),start,start.AddMinutes(5),end,end.AddSeconds(1)})
            { Write(paths,Record(t,20)); events.Append(new AppEvent(t,EventTypes.AppStart)); }
            var rows=store.Load("test-usage","p",start,index:false,until:end);
            Check(rows.Count==3 && rows[0].T==start && rows[^1].T==end,"history bounds wrong");
            Check(store.IndexCount==0,"read-only bounds grew index");
            var storedEvents=events.Load(start,end);
            Check(storedEvents.Count==3 && storedEvents.All(e=>e.T>=start&&e.T<=end),"event bounds wrong");
        }));
        Test("cross-month sessions stay whole, selectable from either month and immutable", () => WithRoot(paths =>
        {
            var at=DailyQuota.Midnight(new DateTime(2026,10,1),TimeZoneInfo.Local);
            var journal=new ChatSessionHistory(paths.Root);
            ChatCacheEntry Entry(string id,DateTimeOffset t) => new(ChatPlatform.Codex,id,id,t,30,"local",false,ActivityAt:t);
            journal.Capture([Entry("a",at.AddMinutes(-10))],at.AddMinutes(-10));
            journal.Capture([Entry("b",at.AddMinutes(10))],at.AddMinutes(10)); journal.Flush(at.AddMinutes(10));
            var restarted=new ChatSessionHistory(paths.Root);
            var sep=restarted.ForRange(new(2026,9,1),new(2026,10,1)).Single();
            var oct=restarted.ForRange(new(2026,10,1),new(2026,11,1)).Single();
            Check(sep.Id==oct.Id && sep.Chats.Count==2 && sep.Start<at && sep.End>at,"month split or filtered session");
            sep.Chats.Clear(); Check(restarted.ForDay(at.LocalDateTime).Single().Chats.Count==2,"UI changed journal");
            Check(restarted.ForRange(new(2026,8,1),new(2026,9,1)).Count==0,"unrelated month linked");
        }));

        HistoryRecord Record(DateTimeOffset at,double used) => new("test-usage","p","Max",at,Statuses.Partial,300,
            new QuotaSnapshot(at.ToString("O"),at,new QuotaLimits(null,UsageParser.Limit(used,midnight.AddDays(20)),UsageParser.Limit(used,midnight.AddDays(20)))));
        void Write(DataPaths paths,HistoryRecord row,string? forceProfile=null)
        {
            var dir=new HistoryStore(paths,false).ProfileDir(row.SourceId,forceProfile??row.ProfileKey); Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir,row.T.ToLocalTime().ToString("yyyy-MM-dd")+".jsonl"),HistoryStore.FormatLine(row)+"\n");
        }
        void WithRoot(Action<DataPaths> body)
        {
            var root=Path.Combine(Path.GetTempPath(),"qw-calendar-test-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try {body(new DataPaths(root));} finally {Directory.Delete(root,true);}
        }
    }
}
