namespace JellyCache.Core;

public enum PrefetchStrategy { Next, Ahead, Season }

// Constructed by the host adapter only from supported, single-file Episode metadata.
// Planning never opens media files or infers episode order from filenames.
public sealed record EpisodeInfo(Guid Id, Guid SeriesId, Guid SeasonId, int SeasonNumber,
    int Number, string SourcePath, int? EndNumber = null);

public static class PrefetchPlan
{
    public static IReadOnlyList<EpisodeInfo> Window(EpisodeInfo current,
        IReadOnlyList<EpisodeInfo> catalog, PrefetchStrategy strategy = PrefetchStrategy.Ahead, int ahead = 2)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!Enum.IsDefined(strategy) || ahead is < 1 or > 50 || catalog.Count is < 1 or > 1000)
            throw new ArgumentException("Invalid bounded planning settings.");
        Validate(current);
        var season = catalog.Where(e => e.SeriesId == current.SeriesId && e.SeasonId == current.SeasonId).ToArray();
        foreach (var episode in season)
        {
            Validate(episode);
            if (episode.SeasonNumber != current.SeasonNumber)
                throw new ArgumentException("Conflicting season numbering.");
        }
        if (season.Select(e => e.Id).Distinct().Count() != season.Length
            || season.Select(e => e.Number).Distinct().Count() != season.Length
            || season.Select(e => e.SourcePath).Distinct(StringComparer.Ordinal).Count() != season.Length
            || season.SingleOrDefault(e => e.Id == current.Id) != current)
            throw new ArgumentException("Ambiguous or incomplete current-episode metadata.");
        var following = season.Where(e => e.Number > current.Number).OrderBy(e => e.Number);
        var count = strategy switch
        {
            PrefetchStrategy.Next => 1,
            PrefetchStrategy.Ahead => ahead,
            _ => 1000
        };
        return Array.AsReadOnly(following.Take(count).ToArray());
    }

    private static void Validate(EpisodeInfo episode)
    {
        if (episode.Id == Guid.Empty || episode.SeriesId == Guid.Empty || episode.SeasonId == Guid.Empty
            || episode.SeasonNumber <= 0 || episode.Number <= 0
            || (episode.EndNumber.HasValue && episode.EndNumber != episode.Number)
            || string.IsNullOrWhiteSpace(episode.SourcePath) || episode.SourcePath.Length > 4096
            || episode.SourcePath.Any(char.IsControl))
            throw new ArgumentException("Unsupported episode metadata.");
    }
}

public sealed record PrefetchDemand(EpisodeInfo Episode, int Distance, IReadOnlyList<string> Sessions);

// An in-memory demand snapshot, not a download queue or cache index. Host events must
// be serialized and reject stale playback events before entering this component.
public sealed class SessionDemand
{
    private readonly object gate = new();
    private readonly Dictionary<string, EpisodeInfo[]> windows = new(StringComparer.Ordinal);
    private readonly List<string> order = [];
    private int nextSession;
    public const int MaximumSessions = 128;

    public void Replace(string session, EpisodeInfo current, IReadOnlyList<EpisodeInfo> catalog,
        PrefetchStrategy strategy = PrefetchStrategy.Ahead, int ahead = 2)
    {
        ValidateSession(session);
        lock (gate)
        {
            // Invalid metadata clears obsolete intent rather than continuing an old window.
            EpisodeInfo[] window;
            try { window = PrefetchPlan.Window(current, catalog, strategy, ahead).ToArray(); }
            catch (ArgumentException) { Remove(session); throw; }
            if (window.Length == 0) { Remove(session); return; }
            if (windows.ContainsKey(session)) { windows[session] = window; return; }
            if (windows.Count >= MaximumSessions) throw new InvalidOperationException("Session limit reached.");
            windows.Add(session, window);
            order.Add(session);
        }
    }

    public void Stop(string session)
    {
        ValidateSession(session);
        lock (gate) Remove(session);
    }

    // Rotate only when the worker asks for a scheduling turn, never for a UI refresh.
    // Ready/in-flight files are supplied by the cache owner and are never scheduled twice.
    public IReadOnlyList<PrefetchDemand> Snapshot(IReadOnlySet<Guid>? excluded = null, bool advance = false)
    {
        lock (gate)
        {
            if (order.Count == 0) return Array.Empty<PrefetchDemand>();
            var rotated = Enumerable.Range(0, order.Count)
                .Select(i => order[(nextSession + i) % order.Count]).ToArray();
            var entries = rotated.SelectMany(session => windows[session]
                .Select((episode, distance) => (session, episode, distance))).ToArray();
            // Conflicting identity/path metadata across sessions must not choose a random version.
            var conflicts = entries.GroupBy(e => e.episode.Id)
                .Where(g => g.Select(e => e.episode).Distinct().Count() != 1)
                .Select(g => g.Key).ToHashSet();
            var pathConflicts = entries.GroupBy(e => e.episode.SourcePath, StringComparer.Ordinal)
                .Where(g => g.Select(e => e.episode.Id).Distinct().Count() != 1)
                .SelectMany(g => g.Select(e => e.episode.Id));
            conflicts.UnionWith(pathConflicts);
            var result = entries.OrderBy(e => e.distance).GroupBy(e => e.episode.Id)
                .Where(g => !conflicts.Contains(g.Key) && !(excluded?.Contains(g.Key) ?? false))
                .Select(g => new PrefetchDemand(g.First().episode, g.Min(e => e.distance) + 1,
                    Array.AsReadOnly(g.Select(e => e.session).Distinct(StringComparer.Ordinal).ToArray())))
                .ToArray();
            if (advance) nextSession = (nextSession + 1) % order.Count;
            return Array.AsReadOnly(result);
        }
    }

    private void Remove(string session)
    {
        if (!windows.Remove(session)) return;
        var index = order.IndexOf(session);
        order.RemoveAt(index);
        if (index < nextSession) nextSession--;
        if (nextSession >= order.Count) nextSession = 0;
    }

    private static void ValidateSession(string session)
    {
        if (string.IsNullOrWhiteSpace(session) || session.Length > 128 || session.Any(char.IsControl))
            throw new ArgumentException("Invalid session identity.");
    }
}
