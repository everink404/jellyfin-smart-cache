namespace JellyCache.Core;

public sealed record PlaybackEvent(string Session, string Playback, Guid Item, string? SourceId, bool Stopped, long Sequence);

// Start establishes identity; delayed progress/stop from an old playback cannot replace it.
public sealed class PlaybackInbox(TimeProvider? clock = null)
{
    private readonly object gate = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<string, (string Playback, DateTimeOffset Seen)> active = [];
    private readonly Dictionary<string, PlaybackEvent> pending = [];
    private long sequence;
    public bool Capture(string session, string playback, Guid item, string? sourceId, string phase)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(session) || session.Length > 128 || session.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(playback) || playback.Length > 128 || playback.Any(char.IsControl)
                || phase is not ("start" or "progress" or "stop")) return false;
            if (phase != "start" && (!active.TryGetValue(session, out var old) || old.Playback != playback)) return false;
            if (!active.ContainsKey(session) && active.Count >= SessionDemand.MaximumSessions) return false;
            // Bound pending as well as active sessions during a burst of stop/start events.
            if (!pending.ContainsKey(session) && pending.Count >= SessionDemand.MaximumSessions) return false;
            if (phase == "stop") active.Remove(session);
            else active[session] = (playback, time.GetUtcNow());
            pending[session] = new(session, playback, item, sourceId, phase == "stop", ++sequence);
            return true;
        }
    }
    public PlaybackEvent[] Drain()
    {
        lock (gate)
        {
            var result = pending.Values.OrderBy(e => e.Sequence).ToArray();
            pending.Clear(); return result;
        }
    }
    public string[] Expire(TimeSpan timeout)
    {
        lock (gate)
        {
            var expired = active.Where(p => time.GetUtcNow() - p.Value.Seen > timeout).Select(p => p.Key).ToArray();
            foreach (var session in expired) { active.Remove(session); pending.Remove(session); }
            return expired;
        }
    }
    public void Clear() { lock (gate) { active.Clear(); pending.Clear(); } }
}
