using System;
using System.Collections.Generic;
using System.Linq;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public partial class App
{
    readonly Dictionary<string, DateTimeOffset> _capacityNotified = new();

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
}
