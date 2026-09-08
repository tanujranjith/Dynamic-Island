using DynamicIsland.Windows.Models;
namespace DynamicIsland.Windows.Infrastructure;

public sealed class NotificationSnapshotTracker
{
    private HashSet<string>? _seen;
    public IReadOnlyList<NotificationInfo> Observe(IEnumerable<NotificationInfo> snapshot)
    {
        var items = snapshot.DistinctBy(n => n.Identity).OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).ToArray();
        var fresh = _seen is null ? [] : items.Where(n => !_seen.Contains(n.Identity)).ToArray();
        _seen = items.Select(n => n.Identity).ToHashSet();
        return fresh;
    }
    public void Reset() => _seen = null;
}

public sealed class NotificationQueue(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly List<NotificationGroup> _pending = [];
    public int Count => _pending.Count;
    public void Enqueue(IEnumerable<NotificationHistoryItem> batch)
    {
        foreach (var group in batch.GroupBy(n => string.IsNullOrEmpty(n.AppId) ? n.App : n.AppId))
            _pending.Add(new(group.OrderBy(n => n.CreatedAt).ToArray(), _clock.GetUtcNow()));
        if (_pending.Count > 50) _pending.RemoveRange(0, _pending.Count - 50);
    }
    public NotificationGroup? Take(bool blocked)
    {
        if (blocked || _pending.Count == 0) return null;
        var cutoff = _clock.GetUtcNow().AddSeconds(-30);
        var stale = _pending.Where(g => g.EnqueuedAt < cutoff).ToArray();
        if (stale.Length > 0)
        {
            _pending.RemoveAll(g => g.EnqueuedAt < cutoff);
            return new(stale.SelectMany(g => g.Items).OrderBy(i => i.CreatedAt).ToArray(), _clock.GetUtcNow(), true);
        }
        var next = _pending[0]; _pending.RemoveAt(0); return next;
    }
    public void Remove(Guid id)
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var remaining = _pending[i].Items.Where(n => n.Id != id).ToArray();
            if (remaining.Length == 0) _pending.RemoveAt(i);
            else _pending[i] = _pending[i] with { Items = remaining };
        }
    }
    public void Clear() => _pending.Clear();
}
