using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public partial class App
{
    readonly Dictionary<string, DateTimeOffset> _capacityNotified = new();
    readonly Dictionary<string, ChatCacheEntry> _capacityPending = new();
    SimpleLog? _notificationLog;
    string? _capacityDelivery;

    IReadOnlyList<ChatCacheEntry> TakeCapacityAlerts(DateTimeOffset now)
    {
        if (_opts.Demo || _opts.Snapshot is not null || !_model.Settings.CacheRemindersEnabled) return [];
        var alerts = CacheEntries(now).Where(e => e.CapacityBlocked && e.CapacityBlockedAt <= now &&
            (!_capacityNotified.TryGetValue(e.Id, out var notified) || e.CapacityBlockedAt > notified)).ToArray();
        foreach (var entry in alerts) _capacityNotified[entry.Id] = entry.CapacityBlockedAt!.Value;
        foreach (var key in _capacityNotified.OrderByDescending(x => x.Value).Skip(256).Select(x => x.Key).ToArray())
            _capacityNotified.Remove(key);
        return alerts;
    }

    IReadOnlyList<ChatCacheEntry> StageCapacityAlerts(DateTimeOffset now)
    {
        var current=_model.Settings.CacheRemindersEnabled?CacheEntries(now).Where(e=>e.CapacityBlocked).ToArray():[];
        var alerts=TakeCapacityAlerts(now);
        foreach(var entry in alerts)_capacityPending[entry.Id]=entry;
        foreach(var id in _capacityPending.Keys.Where(id=>!current.Any(e=>e.Id==id&&e.CapacityBlockedAt==_capacityPending[id].CapacityBlockedAt)).ToArray())
            _capacityPending.Remove(id);
        return alerts;
    }

    void UpdateCapacityNotifications(DateTimeOffset now)
    {
        if(_opts.Demo||_opts.Snapshot is not null||_tray is null)return;
        _notificationLog??=new SimpleLog(Path.Combine(_paths.LogsDir,"notifications.log"));
        try
        {
            var alerts=StageCapacityAlerts(now);
            var current=_model.Settings.CacheRemindersEnabled?CacheEntries(now).Where(e=>e.CapacityBlocked).ToArray():[];
            _tray.SynchronizeCapacity(current);
            if(alerts.Count>0)
            {
                _notificationLog.Write("detected count="+alerts.Count);
                _notificationLog.Write(CapacitySound.Play()?"sound requested":"sound failed");
            }
            var delivery=_capacityPending.Count==0?"idle":NotificationEnvironment.DeferVisual()?"visual deferred presentation":"visual pending";
            if(delivery!=_capacityDelivery){_notificationLog.Write(delivery);_capacityDelivery=delivery;}
            if(delivery=="visual pending"&&_tray.AlertCapacity(_capacityPending.Values.ToArray(),_notificationLog.Write))
            {_capacityPending.Clear();_capacityDelivery="idle";}
        }
        catch(Exception e){_notificationLog.Write("delivery failed "+e.GetType().Name);}
    }
}
