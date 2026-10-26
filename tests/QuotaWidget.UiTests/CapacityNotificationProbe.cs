using System.Reflection;
using System.IO;
using System.Text;
using System.Windows;
using QuotaWidget.App;
using QuotaWidget.Core;

static class CapacityNotificationProbe
{
    public static void Run(App app)
    {
        void Check(bool value,string message){if(!value)throw new Exception("Capacity notifications: "+message);}
        foreach(var state in Enumerable.Range(0,9))
        {
            Check(NotificationEnvironment.DeferVisual(state,true),"fullscreen allowed a visual alert");
            Check(NotificationEnvironment.DeferVisual(state,false)==(state!=5),"presentation/DND state mishandled");
        }
        var screen=new Rect(-1920,0,1920,1080);
        Check(NotificationEnvironment.CoversMonitor(screen,screen,false,false,false),"borderless fullscreen not suppressed");
        Check(!NotificationEnvironment.CoversMonitor(screen,screen,true,true,false),"normal maximized window suppressed");
        Check(!NotificationEnvironment.CoversMonitor(screen,screen,false,false,true),"desktop treated as a fullscreen game");
        Check(!NotificationEnvironment.CoversMonitor(new Rect(-1800,10,1600,900),screen,false,false,false),"windowed app suppressed");
        var wav=CapacitySound.Wave();
        Check(Encoding.ASCII.GetString(wav,0,4)=="RIFF"&&Encoding.ASCII.GetString(wav,8,4)=="WAVE"&&BitConverter.ToInt32(wav,40)==wav.Length-44,"invalid chime WAV");
        Check(wav.Skip(44).Any(x=>x!=0),"chime is silent");
        var now=DateTimeOffset.Now;
        var blocked=new ChatCacheEntry(ChatPlatform.Codex,"capacity-notice-test","Synthetic stopped chat",now,120,"fixture",false,CapacityBlockedAt:now);
        var notice=new CapacityNotice(()=>{},()=>true);notice.Add([blocked]);
        Check(!notice.Present()&&!notice.IsVisible&&!notice.IsLoaded,"fullscreen deferred notice created a visible window");
        Check(!notice.ShowActivated&&!notice.ShowInTaskbar&&!notice.Focusable,"passive notice requests activation/taskbar presence");
        notice.Synchronize([]);Check(!notice.IsVisible,"recovered pending notice remained visible");
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var names=new[]{"_model","_opts","_cacheEntries","_currentChatSession","_chatLifecycle","_capacityPending","_capacityNotified"};
        var saved=names.ToDictionary(n=>n,n=>typeof(App).GetField(n,flags)!.GetValue(app));
        void Set(string name,object? value)=>typeof(App).GetField(name,flags)!.SetValue(app,value);
        var root=Path.Combine(Path.GetTempPath(),"qw-notifications-"+Guid.NewGuid());
        try
        {
            var model=new WidgetModel(new DataPaths(root),false){ReadOnly=true};model.Initialize();
            Set("_model",model);Set("_opts",new AppOptions());Set("_currentChatSession",null);Set("_chatLifecycle",ChatLifecycleSnapshot.Empty);
            // Existing dictionaries are readonly references; preserve and restore
            // their contents rather than replacing application fields.
            var pending=(Dictionary<string,ChatCacheEntry>)saved["_capacityPending"]!;
            var notified=(Dictionary<string,DateTimeOffset>)saved["_capacityNotified"]!;
            var oldPending=pending.ToArray();var oldNotified=notified.ToArray();pending.Clear();notified.Clear();
            try
            {
                var stage=typeof(App).GetMethod("StageCapacityAlerts",flags)!;
                IReadOnlyList<ChatCacheEntry> Stage()=>(IReadOnlyList<ChatCacheEntry>)stage.Invoke(app,[now])!;
                Set("_cacheEntries",new[]{blocked});Check(Stage().Count==1&&pending.Count==1,"new failure was not queued");
                Check(Stage().Count==0&&pending.Count==1,"deferred delivery lost the queue or repeated the sound trigger");
                Set("_cacheEntries",new[]{blocked with{CapacityBlockedAt=null,Running=true,ActivityAt=now.AddSeconds(1)}});
                Check(Stage().Count==0&&pending.Count==0,"recovered task would show a delayed stale alert");
                Set("_cacheEntries",new[]{blocked with{CapacityBlockedAt=now.AddSeconds(-.1)}});Check(Stage().Count==0,"old replay generated a new alert");
                Set("_cacheEntries",new[]{blocked with{Id="next"}});Check(Stage().Count==1&&pending.Count==1,"independent new failure lost");
                model.Settings.CacheRemindersEnabled=false;Check(Stage().Count==0&&pending.Count==0,"disabling reminders retained a pending popup");
            }
            finally
            {
                pending.Clear();foreach(var p in oldPending)pending[p.Key]=p.Value;
                notified.Clear();foreach(var p in oldNotified)notified[p.Key]=p.Value;
            }
        }
        finally
        {
            foreach(var pair in saved.Where(p=>p.Key is not ("_capacityPending" or "_capacityNotified")))Set(pair.Key,pair.Value);
            Directory.Delete(root,true);
        }
        Console.WriteLine("PASS: passive capacity notice, fullscreen/DND/desktop/maximized policy, deferred queue recovery/deduplication/disable and valid non-silent chime. No sound played or popup shown.");
    }
}
