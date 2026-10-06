namespace QuotaWidget.Core;

public sealed record QuotaToken(DateTimeOffset At,DateTimeOffset End,string Chat,string Model,double Input,double Cached,double Output,bool Conflict=false);
public sealed record ChatQuotaShare(string Chat,string Model,double Points,double Spread);
public sealed record ChatQuotaEstimate(double Observed,IReadOnlyList<ChatQuotaShare> Shares,string Reason,int CalibrationHours=0,double ValidationError=0)
{
    public bool Available=>Reason.Length==0;
    public static ChatQuotaEstimate Unknown(string reason,double observed=0)=>new(observed,[],reason);
}

/// <summary>Local empirical allocation, not a provider bill. Fit nonnegative per-model
/// IN/CACHE/OUT weights, validate on held-out hours, and reject unstable chat shares.
/// Quota observations supply the total; no API dollar-to-subscription conversion.</summary>
public static class ChatQuotaEstimator
{
    sealed record Window(DateTimeOffset Start,DateTimeOffset End,double Points);
    sealed record Fit(double[] Weights,double Error);

    public static ChatQuotaEstimate Build(SeriesData quota,IReadOnlyList<QuotaToken> tokens,
        DateTimeOffset start,DateTimeOffset end,bool ready=true)
    {
        if(!ready)return ChatQuotaEstimate.Unknown("本机记录尚未完整");
        var segments=quota.Segments.Where(s=>s.End<=end).ToArray();
        var selected=Windows(segments.Where(s=>s.Start>=start),false);
        var observed=selected.Sum(w=>w.Points);
        if(observed<3||selected.Sum(w=>(w.End-w.Start).TotalMinutes)<45)
            return ChatQuotaEstimate.Unknown("消耗或观测时长不足",observed);
        bool Contains(Window w,QuotaToken t)=>t.At>w.Start&&t.At<=w.End;
        var relevant=tokens.Where(t=>selected.Any(w=>Contains(w,t))).ToArray();
        if(relevant.Length==0||relevant.Any(t=>t.Conflict||string.IsNullOrWhiteSpace(t.Model)))
            return ChatQuotaEstimate.Unknown("用量记录缺失或冲突",observed);
        // Historical selections use only data available at their endpoint. Do not fit
        // across arbitrarily old plan/pricing regimes for an all-history allocation.
        var calibrationStart=end.AddDays(-8);
        if(start<calibrationStart||selected[0].Start<calibrationStart)return ChatQuotaEstimate.Unknown("所选历史超出校准范围",observed);
        var hours=Windows(segments.Where(s=>s.Start>=calibrationStart),true);
        var modelNames=tokens.Where(t=>t.At>=calibrationStart&&t.At<=end).Select(t=>t.Model).Distinct().Order().ToArray();
        if(modelNames.Length is 0 or >8)return ChatQuotaEstimate.Unknown("模型组合缺少可用校准",observed);
        var models=modelNames.Select((m,i)=>(m,i)).ToDictionary(x=>x.m,x=>x.i);
        int size=models.Count*3;
        double[] Features(IEnumerable<QuotaToken> rows)
        {
            var x=new double[size];
            foreach(var r in rows)
            {
                if(!models.TryGetValue(r.Model,out var m))continue;
                x[m*3]+=r.Input/1e6;x[m*3+1]+=r.Cached/1e6;x[m*3+2]+=r.Output/1e6;
            }
            return x;
        }
        var data=hours.Select(w=>(Window:w,Rows:tokens.Where(t=>Contains(w,t)).ToArray()))
            .Where(a=>a.Rows.All(t=>!t.Conflict&&t.End<=a.Window.End.AddMinutes(5)))
            .Select(a=>(X:Features(a.Rows),Y:a.Window.Points)).ToArray();
        var informative=data.Count(a=>a.X.Any(x=>x>0));
        if(informative<Math.Max(8,models.Count*4)||data.Sum(a=>a.Y)<12)
            return ChatQuotaEstimate.Unknown("等待更多可校准的历史",observed);
        var scales=Enumerable.Range(0,size).Select(j=>Math.Sqrt(data.Sum(a=>a.X[j]*a.X[j]))).ToArray();
        var x=data.Select(a=>a.X.Select((v,j)=>scales[j]>0?v/scales[j]:0).ToArray()).ToArray();
        var y=data.Select(a=>a.Y).ToArray();
        var fits=new List<Fit>();
        // Different initial token components expose collinear solutions. Contiguous
        // holdouts test changes in workload and pricing rather than memorizing jumps.
        foreach(var seed in new[]{0,1,2,3,4,5})
        foreach(var ridge in new[]{0d,.01})
        {
            double abs=0,max=0;int count=0;bool valid=true;
            for(var fold=0;fold<3;fold++)
            {
                bool Test(int i)=>i*3/data.Length==fold;
                var train=Enumerable.Range(0,data.Length).Where(i=>!Test(i)).ToArray();
                var w=Solve(x,y,train,seed,ridge);
                foreach(var i in Enumerable.Range(0,data.Length).Where(Test))
                {
                    if(y[i]==0&&!x[i].Any(v=>v>0))continue;
                    var error=Math.Abs(Dot(x[i],w)-y[i]);abs+=error;max=Math.Max(max,error);count++;
                    if(error>Math.Max(2,y[i]*.5+1))valid=false;
                }
            }
            var mae=abs/Math.Max(1,count);
            if(!valid||mae>Math.Max(.8,y.Average()*.25))continue;
            var full=Solve(x,y,Enumerable.Range(0,data.Length).ToArray(),seed,ridge);
            fits.Add(new(full.Select((v,j)=>scales[j]>0?v/scales[j]:0).ToArray(),mae));
        }
        if(fits.Count<3)return ChatQuotaEstimate.Unknown("额度与本机用量暂不吻合",observed);
        var groups=relevant.GroupBy(t=>(t.Chat,t.Model)).ToArray();
        var allocations=new List<double[]>();
        foreach(var fit in fits)
        {
            var shares=new double[groups.Length];bool valid=true;
            foreach(var window in selected)
            {
                var rows=relevant.Where(t=>Contains(window,t)).ToArray();
                if(rows.Any(t=>t.End>end)){valid=false;break;}
                var prediction=Dot(Features(rows),fit.Weights);
                if(window.Points>0&&prediction<=0||Math.Abs(prediction-window.Points)>Math.Max(1.5,window.Points*.3))
                {valid=false;break;}
                if(window.Points==0)continue;
                for(var i=0;i<groups.Length;i++)
                    shares[i]+=window.Points*Dot(Features(groups[i].Where(t=>Contains(window,t))),fit.Weights)/prediction;
            }
            if(valid)allocations.Add(shares);
        }
        if(allocations.Count<3)return ChatQuotaEstimate.Unknown("所选区间无法可靠分摊",observed);
        var result=groups.Select((g,i)=>new ChatQuotaShare(g.Key.Chat,g.Key.Model,
            allocations.Average(a=>a[i]),allocations.Max(a=>a[i])-allocations.Min(a=>a[i]))).ToArray();
        if(result.Any(s=>s.Spread>Math.Max(.75,s.Points*.3)))
            return ChatQuotaEstimate.Unknown("不同合理权重的分摊差异过大",observed);
        return new(observed,result,"",hours.Count,fits.Average(f=>f.Error));
    }

    static double Dot(double[] x,double[] w)=>x.Zip(w,(a,b)=>a*b).Sum();
    static double[] Solve(double[][] x,double[] y,int[] rows,int seed,double ridge)
    {
        var n=x[0].Length;var w=new double[n];
        var gram=Enumerable.Range(0,n).Select(j=>Enumerable.Range(0,n).Select(k=>rows.Sum(i=>x[i][j]*x[i][k])).ToArray()).ToArray();
        var target=Enumerable.Range(0,n).Select(j=>rows.Sum(i=>x[i][j]*y[i])).ToArray();
        // Coordinate descent is deterministic and bounded; selected zero columns stay zero.
        var order=Enumerable.Range(0,n).OrderBy(j=>j%3==seed%3?0:1).ThenBy(j=>seed<3?j/3:-j/3).ToArray();
        for(var pass=0;pass<600;pass++)
        {
            double change=0;
            foreach(var j in order)
            {
                var norm=gram[j][j];if(norm<1e-16)continue;
                var next=Math.Max(0,(target[j]-Dot(gram[j],w)+norm*w[j])/(norm+ridge));
                var delta=next-w[j];w[j]=next;change=Math.Max(change,Math.Abs(delta));
            }
            if(change<1e-7)break;
        }
        return w;
    }
    static List<Window> Windows(IEnumerable<RateSegment> source,bool hourly)
    {
        var result=new List<Window>();DateTimeOffset? from=null,to=null;double amount=0;int group=0;
        void Flush(){if(from is {} a&&to is {} b&&(!hourly||(b-a).TotalMinutes>=45))result.Add(new(a,b,amount));from=to=null;amount=0;}
        foreach(var s in source)
        {
            if(!s.Valid||s.StartsAtCapacity||s.Minutes<=0||!double.IsFinite(s.Delta)||s.Delta<0){Flush();continue;}
            if(to is {} last&&(last!=s.Start||group!=s.Group))Flush();
            from??=s.Start;to=s.End;group=s.Group;amount+=s.Delta;
            if(hourly&&(to.Value-from.Value).TotalMinutes>=60)Flush();
        }
        Flush();return result;
    }
}
