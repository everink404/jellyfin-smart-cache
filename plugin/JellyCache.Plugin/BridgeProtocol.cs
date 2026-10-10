namespace JellyCache.Plugin;

// Shared with the dependency-free contract harness. Playback callbacks do no I/O.
public sealed record PendingEvent<T>(string Session, string Playback, long Sequence, string Phase, T Value);

public sealed class EventBuffer<T>
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _active = new();
    private readonly Dictionary<string, PendingEvent<T>> _pending = new();
    private long _sequence = DateTime.UtcNow.Ticks;

    public bool Put(string session, string playback, string phase, T value)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(session) || session.Length > 128 ||
                string.IsNullOrEmpty(playback) || !new[] { "start", "progress", "pause", "stop" }.Contains(phase))
                return false;
            if (!_active.ContainsKey(session) && _active.Count >= 128) return false;
            if (phase != "start" && _active.TryGetValue(session, out var previous) && previous != playback)
                return false; // A delayed stop/progress for the previous episode must not cancel a new start.
            _active[session] = playback;
            _sequence = Math.Max(_sequence + 1, DateTime.UtcNow.Ticks);
            _pending[session] = new(session, playback, _sequence, phase, value);
            return true;
        }
    }

    public PendingEvent<T>[] Drain()
    {
        lock (_gate)
        {
            var result = _pending.Values.OrderBy(e => e.Sequence).ToArray();
            _pending.Clear();
            return result;
        }
    }
}

public static class BridgeProtocol
{
    public static Uri Endpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.Host != "127.0.0.1" || uri.AbsolutePath != "/v1/events" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new ArgumentException("Bridge endpoint must be http://127.0.0.1:PORT/v1/events");
        return uri;
    }

    public static string RelativePath(string path, string root)
    {
        root = root.TrimEnd('/');
        if (!root.StartsWith('/') || root.Length == 0 || root.Split('/').Contains("..") ||
            !path.StartsWith(root + "/", StringComparison.Ordinal))
            throw new ArgumentException("Media path outside configured root");
        var result = path[(root.Length + 1)..];
        if (result.Length == 0 || result.Length > 4096 || result.Contains('\\') || result.Contains('\0') ||
            result.Split('/').Any(p => p is "" or "." or "..") || result.Split('/')[0].Contains(':'))
            throw new ArgumentException("Ambiguous media path");
        return result;
    }
}
