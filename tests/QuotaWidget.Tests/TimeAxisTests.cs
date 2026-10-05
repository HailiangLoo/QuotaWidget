using QuotaWidget.Core;

static class TimeAxisTests
{
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        var t=DateTimeOffset.Parse("2026-10-04T00:00:00+08:00");
        void Check(bool b,string why){if(!b)throw new Exception(why);}
        void Test(string name,Action body)=>tests.Add(("time axis: "+name,()=>{body();return Task.CompletedTask;}));
        void Layout(IReadOnlyList<AxisTick> ticks,double width)
        {
            Check(ticks.Count<=6,"excess labels");
            Check(ticks.All(p=>p.Left>=0&&p.Left+p.Width<=width),"labels leave chart");
            foreach(var (a,b) in ticks.Zip(ticks.Skip(1)))
                Check(a.Time<b.Time&&a.Left+a.Width+8<=b.Left,"unsorted/overlapping labels");
        }
        Test("stage edges and rate peaks retain exact timestamps and available range labels",()=>
        {
            var stage=t.AddMinutes(43.408);var peak=t.AddMinutes(73.3);
            var landmarks=TimeAxis.RateLandmarks([stage],[new(peak,4,3,false),new(t.AddMinutes(24),99,99,true)]).ToArray();
            Check(landmarks.All(p=>p.Time!=t.AddMinutes(24)),"clipping edge masquerades as a rate crest");
            var ticks=TimeAxis.Select(t,t.AddHours(2),300,landmarks,_=>30);
            Layout(ticks,300);
            Check(ticks[0].Time==t&&ticks[^1].Time==t.AddHours(2),"range changed");
            Check(ticks.Any(p=>p.Time==stage&&p.Kind==AxisLandmarkKind.Stage)&&ticks.Any(p=>p.Time==peak&&p.Kind==AxisLandmarkKind.Peak),"meaningful timestamps were rounded away");
        });
        Test("density follows width and clustered activity never crowds the rest of the axis",()=>
        {
            var dense=Enumerable.Range(0,1000).Select(i=>new AxisLandmark(t.AddMinutes(55+i*.01),AxisLandmarkKind.Stage,3)).ToArray();
            foreach(var width in new[]{216d,300,340,500})
            {
                var ticks=TimeAxis.Select(t,t.AddHours(2),width,dense,_=>30);Layout(ticks,width);
                Check(ticks.Count>=3&&ticks.Count<=4,"dense activity produced fillers in empty time");
                Check(ticks.Where(p=>p.Kind==AxisLandmarkKind.Regular).All(p=>p.Time==t||p.Time==t.AddHours(2)),"invented an idle landmark to fill a slot");
                Check(ticks.Count(p=>p.Kind!=AxisLandmarkKind.Regular)<=2,"dense events took over the axis");
            }
            var crowded=TimeAxis.Select(t,t.AddHours(2),180,dense,_=>55);Layout(crowded,180);
            Check(crowded.Count<5,"forced five wide labels to fit");
        });
        Test("separated activity blocks are both represented without labels in the idle middle",()=>
        {
            // The reported 24h shape: many short stages in the middle, a small cluster
            // near the right edge, and nothing happening between them.
            var left=Enumerable.Range(0,80).Select(i=>new AxisLandmark(t.AddMinutes(430+i*4),AxisLandmarkKind.Stage,3));
            var right=new[]{new AxisLandmark(t.AddMinutes(1225),AxisLandmarkKind.Stage,3),new AxisLandmark(t.AddMinutes(1280),AxisLandmarkKind.Peak,2)};
            foreach(var width in new[]{216d,280,340,500})
            {
                var ticks=TimeAxis.Select(t,t.AddDays(1),width,left.Concat(right),_=>31);Layout(ticks,width);
                Check(ticks.Any(p=>p.Time>=t.AddMinutes(430)&&p.Time<=t.AddMinutes(746)),"lost middle activity");
                Check(ticks.Any(p=>p.Time>=t.AddMinutes(1225)&&p.Time<=t.AddMinutes(1280)),"lost right activity to an endpoint or uniform slot");
                Check(ticks.All(p=>p.Time<=t.AddMinutes(746)||p.Time>=t.AddMinutes(1225)),"tick in empty middle");
                Check(ticks.Where(p=>p.Kind!=AxisLandmarkKind.Regular).All(p=>Math.Abs(p.X-(p.Time-t).TotalDays*width)<1e-8),"shifted event time to make the text fit");
            }
        });
        Test("continuous wavy rates keep only a few major time labels",()=>
        {
            var peaks=Enumerable.Range(1,47).Select(i=>new ChartPeak(t.AddMinutes(i*30),i==12||i==35?5:2,i==12||i==35?4:.2,false));
            var marks=TimeAxis.RateLandmarks([],peaks).ToArray();
            foreach(var width in new[]{216d,280,300,340,500})
            {
                var ticks=TimeAxis.Select(t,t.AddDays(1),width,marks,_=>28);Layout(ticks,width);
                Check(ticks.Count<=(width<=300?4:width<=340?5:6),"continuous curve filled all available space with labels");
                Check(ticks.Any(p=>p.Time==t.AddHours(6))&&ticks.Any(p=>p.Time==t.AddMinutes(1050)),"major crests lost to minor wiggles");
                Check(ticks.Where(p=>p.Kind==AxisLandmarkKind.Regular).All(p=>p.Time==t||p.Time==t.AddDays(1)),"returned uniform idle filler ticks");
            }
        });
        Test("continuous cumulative labels do not all cluster around the largest early increments",()=>
        {
            var data=new SeriesData{Key=SeriesKey.Total,Segments=Enumerable.Range(0,144).Select(i=>new RateSegment
                {Start=t.AddMinutes(i*10),End=t.AddMinutes((i+1)*10),Delta=i%2==0?(i<48?2:1):0}).ToList()};
            var marks=TimeAxis.CumulativeLandmarks(CumulativeSeries.Build(data,t,t.AddDays(1)));
            var ticks=TimeAxis.Select(t,t.AddDays(1),280,marks,_=>28);Layout(ticks,280);
            Check(ticks.Count<=4&&ticks.Any(p=>p.Time>=t.AddHours(8)&&p.Time<=t.AddHours(17)),"middle of a continuous day is unreadable");
            Check(ticks.Zip(ticks.Skip(1)).All(p=>p.Second.X-p.First.X>=60),"selected labels cluster despite the lower count");
        });
        Test("cumulative long plateaus get no filler ticks and both rises are represented",()=>
        {
            var data=new SeriesData{Key=SeriesKey.Total,Segments=[]};
            foreach(var (a,b,d) in new[]{(0,100,0),(100,130,2),(130,1200,0),(1200,1230,1),(1230,1440,0)})
                data.Segments.Add(new(){Start=t.AddMinutes(a),End=t.AddMinutes(b),Delta=d});
            var marks=TimeAxis.CumulativeLandmarks(CumulativeSeries.Build(data,t,t.AddDays(1)));
            foreach(var width in new[]{216d,280,340})
            {
                var ticks=TimeAxis.Select(t,t.AddDays(1),width,marks,_=>31);Layout(ticks,width);
                Check(ticks.Any(p=>p.Time>=t.AddMinutes(100)&&p.Time<=t.AddMinutes(130)),"lost first rise near range edge");
                Check(ticks.Any(p=>p.Time>=t.AddMinutes(1200)&&p.Time<=t.AddMinutes(1230)),"lost resumed rise");
                Check(ticks.All(p=>p.Time<=t.AddMinutes(130)||p.Time>=t.AddMinutes(1200)),"flat plateau decorated with arbitrary times");
            }
        });
        Test("flat views only need range context and near-edge events may replace a range label",()=>
        {
            var empty=TimeAxis.Select(t,t.AddHours(2),300,[],_=>31);Layout(empty,300);
            Check(empty.Count==2&&empty[0].Time==t&&empty[1].Time==t.AddHours(2),"empty chart grew interior ticks");
            var eventAt=t.AddMinutes(119);
            var near=TimeAxis.Select(t,t.AddHours(2),216,[new(eventAt,AxisLandmarkKind.Stage,3)],_=>31);Layout(near,216);
            Check(near.Any(p=>p.Time==eventAt)&&near.All(p=>p.Time!=t.AddHours(2)),"decorative range label displaced actual near-edge stage");
            Check(TimeAxis.Select(t,t.AddHours(2),20,[],_=>31).Count==0,"unmeasurable host overlaps labels");
        });
        Test("cumulative landmarks use starts and plateaus of measured rises instead of rate crests",()=>
        {
            var data=new SeriesData{Key=SeriesKey.Total,Segments=[]};
            foreach(var (a,b,d,g) in new[]{(0,10,0,0),(10,20,1,0),(20,30,2,0),(30,50,0,0),(50,60,4,0),(60,70,0,0),(75,85,2,1)})
                data.Segments.Add(new(){Start=t.AddMinutes(a),End=t.AddMinutes(b),Delta=d,Group=g});
            var cumulative=CumulativeSeries.Build(data,t,t.AddMinutes(90));
            var marks=TimeAxis.CumulativeLandmarks(cumulative);
            Check(marks.Select(p=>p.Time).SequenceEqual(new[]{10,30,50,60,75,85}.Select(m=>t.AddMinutes(m))),"lost rising/plateau boundaries or bridged a data gap");
            Check(marks.All(p=>p.Kind==AxisLandmarkKind.Accumulation)&&marks.All(p=>p.Time!=t.AddMinutes(20)),"rate peak or each quota increment became a cumulative landmark");
            Check(cumulative.Segments[^1].To==9,"axis selection changed cumulative data");
        });
        Test("out-of-range and duplicate landmarks do not change the range or ordering",()=>
        {
            AxisLandmark[] marks=[new(t.AddMinutes(-5),AxisLandmarkKind.Stage,100),new(t.AddMinutes(30),AxisLandmarkKind.Peak,2),
                new(t.AddMinutes(30),AxisLandmarkKind.Stage,3),new(t.AddHours(5),AxisLandmarkKind.Stage,100)];
            var a=TimeAxis.Select(t,t.AddHours(2),300,marks,_=>30);
            var b=TimeAxis.Select(t,t.AddHours(2),300,marks.Reverse(),_=>30);
            Check(a.SequenceEqual(b),"input enumeration order changed labels");Layout(a,300);
            Check(a.All(p=>p.Time>=t&&p.Time<=t.AddHours(2)),"borrowed other range's data");
        });
    }
}
