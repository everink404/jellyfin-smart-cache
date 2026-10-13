using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace JellyCache.Core;

public sealed record CacheLimits(long CapacityBytes, long MinimumFreeBytes, long BytesPerSecond = 8 * 1024 * 1024);
public sealed record SourceVersion(long Length, long ModifiedTicks)
{
    public static SourceVersion Read(string path)
    {
        CacheStore.RequirePlainPath(path);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Attributes.HasFlag(FileAttributes.Directory))
            throw new IOException("Source is not a nonempty regular file.");
        return new(file.Length, file.LastWriteTimeUtc.Ticks);
    }
}

public sealed record CacheUsage(long CompleteBytes, long ReservedBytes, int CompleteFiles, int Leases);
public enum CopyOutcome { Published, AlreadyPresent, CapacityBlocked }

// Dedicated, single-owner directory. This is not yet wired into Jellyfin playback.
public sealed class CacheStore : IDisposable
{
    private const string Format = "JellyCache.Core.Store.1";
    private readonly string root;
    private readonly CacheLimits limits;
    private readonly Func<long> availableBytes;
    private readonly FileStream ownership;
    private readonly object gate = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly Dictionary<Guid, int> pins = [];
    private List<Entry> entries = [];
    private long sequence;
    private bool copying, disposed;
    private sealed record Entry(Guid Id, string Source, SourceVersion Version, bool Complete,
        string Hash, SourceVersion? LocalVersion, long LastUse);

    public CacheStore(string directory, CacheLimits limits, Func<long>? availableBytes = null)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Cache root must be absolute.");
        if (limits.CapacityBytes <= 0 || limits.MinimumFreeBytes < 0 || limits.BytesPerSecond <= 0)
            throw new ArgumentException("Invalid cache limits.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (root == Path.GetPathRoot(root)) throw new ArgumentException("A dedicated directory is required.");
        this.limits = limits;
        // Unix DriveInfo keeps the supplied path and queries its filesystem, including bind mounts.
        this.availableBytes = availableBytes ?? (() => new DriveInfo(root).AvailableFreeSpace);
        RequirePlainPath(root);
        Directory.CreateDirectory(root);
        var marker = At("owner.json");
        if (!File.Exists(marker) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("Refusing an unmanaged nonempty directory.");
        RequirePlainPath(At("store.lock"));
        ownership = new FileStream(At("store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (!File.Exists(marker)) File.WriteAllText(marker, JsonSerializer.Serialize(Format));
            RequirePlainPath(marker);
            if (JsonSerializer.Deserialize<string>(File.ReadAllText(marker)) != Format)
                throw new IOException("Unknown cache ownership format.");
            RequirePlainPath(At("index.json"));
            if (File.Exists(At("index.json")))
                entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(At("index.json")))
                    ?? throw new IOException("Invalid cache index.");
            ValidateIndexAndDirectory();
            // A durable incomplete record owns both possible crash leftovers.
            foreach (var entry in entries.ToArray())
            {
                if (!entry.Complete || !IsValidOnDisk(entry, verifyHash: true, requireSource: false)) Delete(entry);
            }
            foreach (var entry in entries.OrderBy(e => e.LastUse).ToArray())
            {
                if (entries.Sum(e => e.Version.Length) <= limits.CapacityBytes) break;
                Delete(entry);
            }
            sequence = entries.Select(e => e.LastUse).DefaultIfEmpty().Max();
            Save();
        }
        catch { ownership.Dispose(); throw; }
    }

    public CacheUsage Usage
    {
        get
        {
            lock (gate)
            {
                EnsureOpen();
                return new(entries.Where(e => e.Complete).Sum(e => e.Version.Length),
                    entries.Where(e => !e.Complete).Sum(e => e.Version.Length),
                    entries.Count(e => e.Complete), pins.Values.Sum());
            }
        }
    }

    public async Task<CopyOutcome> CopyAsync(Guid id, string sourcePath, CancellationToken token = default,
        IReadOnlySet<Guid>? protectedIds = null)
    {
        if (id == Guid.Empty || !Path.IsPathFullyQualified(sourcePath)) throw new ArgumentException("Invalid source identity.");
        var source = Path.GetFullPath(sourcePath);
        if (InsideRoot(source)) throw new ArgumentException("Source cannot be inside the cache.");
        await writer.WaitAsync(token).ConfigureAwait(false);
        Entry? pending = null;
        try
        {
            var version = SourceVersion.Read(source);
            lock (gate)
            {
                EnsureOpen();
                copying = true;
                token.ThrowIfCancellationRequested();
                var old = entries.SingleOrDefault(e => e.Id == id);
                if (old is not null)
                {
                    if (old.Source == source && old.Version == version && IsValidOnDisk(old, false))
                        return CopyOutcome.AlreadyPresent;
                    if (pins.ContainsKey(id)) return CopyOutcome.CapacityBlocked;
                }
                if (version.Length > limits.CapacityBytes || availableBytes() - limits.MinimumFreeBytes < version.Length)
                    return CopyOutcome.CapacityBlocked;
                if (old is not null) Delete(old);
                if (entries.Count >= 10000) return CopyOutcome.CapacityBlocked;
                // Preflight ensures a blocked attempt does not evict files without enough total reclaimable capacity.
                var used = entries.Sum(e => e.Version.Length);
                bool Evictable(Entry e) => !pins.ContainsKey(e.Id) && !(protectedIds?.Contains(e.Id) ?? false);
                var reclaimable = entries.Where(Evictable).Sum(e => e.Version.Length);
                if (used - reclaimable > limits.CapacityBytes - version.Length) return CopyOutcome.CapacityBlocked;
                foreach (var victim in entries.Where(Evictable).OrderBy(e => e.LastUse).ToArray())
                {
                    if (entries.Sum(e => e.Version.Length) <= limits.CapacityBytes - version.Length) break;
                    Delete(victim);
                }
                pending = new(id, source, version, false, "", null, ++sequence);
                entries.Add(pending);
                Save(); // Reserve and journal ownership before creating any data file.
            }
            var watch = Stopwatch.StartNew();
            var buffer = new byte[256 * 1024];
            long copied = 0;
            string hash;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(Data(id, true), FileMode.CreateNew, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                while (copied < version.Length)
                {
                    var size = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, version.Length - copied)), token)
                        .ConfigureAwait(false);
                    if (size == 0) throw new IOException("Source became shorter during copy.");
                    RequirePlainPath(root);
                    if (availableBytes() - limits.MinimumFreeBytes < size) throw new IOException("Disk safety margin reached.");
                    await output.WriteAsync(buffer.AsMemory(0, size), token).ConfigureAwait(false);
                    digest.AppendData(buffer, 0, size);
                    copied += size;
                    var wait = copied / (double)limits.BytesPerSecond - watch.Elapsed.TotalSeconds;
                    if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), token).ConfigureAwait(false);
                }
                if (await input.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
                    throw new IOException("Source grew during copy.");
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                hash = Convert.ToHexString(digest.GetHashAndReset());
            }
            if (SourceVersion.Read(source) != version) throw new IOException("Source changed during copy.");
            await using (var local = File.OpenRead(Data(id, true)))
                if (Convert.ToHexString(await SHA256.HashDataAsync(local, token).ConfigureAwait(false)) != hash)
                    throw new IOException("Copied data failed verification.");
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                RequirePlainPath(root);
                if (SourceVersion.Read(source) != version) throw new IOException("Source changed before publication.");
                File.Move(Data(id, true), Data(id, false));
                var complete = pending with { Complete = true, Hash = hash, LocalVersion = SourceVersion.Read(Data(id, false)) };
                entries[entries.IndexOf(pending)] = complete;
                try { Save(); }
                catch { entries[entries.IndexOf(complete)] = pending; throw; }
                pending = null;
            }
            return CopyOutcome.Published;
        }
        finally
        {
            try
            {
                lock (gate)
                {
                    if (pending is not null && entries.Contains(pending)) Delete(pending);
                }
            }
            finally { lock (gate) copying = false; writer.Release(); }
        }
    }

    // Caller must keep this lease through actual reading/ffmpeg lifetime, including the
    // interval between source resolution and opening the path. Merely returning Path is unsafe.
    public CacheLease? TryAcquire(Guid id, string sourcePath)
    {
        lock (gate)
        {
            EnsureOpen();
            var entry = entries.SingleOrDefault(e => e.Id == id);
            if (entry is null || entry.Source != Path.GetFullPath(sourcePath) || !IsValidOnDisk(entry, false)) return null;
            FileStream stream;
            try { stream = new FileStream(Data(id, false), FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            pins[id] = pins.GetValueOrDefault(id) + 1;
            entries[entries.IndexOf(entry)] = entry with { LastUse = ++sequence };
            return new(Data(id, false), stream, () => Release(id));
        }
    }

    public bool IsReady(Guid id, string sourcePath)
    {
        lock (gate)
        {
            EnsureOpen();
            var entry = entries.SingleOrDefault(e => e.Id == id);
            return entry is not null && entry.Source == Path.GetFullPath(sourcePath) && IsValidOnDisk(entry, false);
        }
    }

    private void Release(Guid id)
    {
        lock (gate)
        {
            if (pins[id] == 1) pins.Remove(id); else pins[id]--;
        }
    }

    private bool IsValidOnDisk(Entry entry, bool verifyHash, bool requireSource = true)
    {
        if (!entry.Complete) return false;
        try
        {
            if ((requireSource && SourceVersion.Read(entry.Source) != entry.Version)
                || SourceVersion.Read(Data(entry.Id, false)) != entry.LocalVersion) return false;
            if (!verifyHash) return true;
            using var stream = File.OpenRead(Data(entry.Id, false));
            return Convert.ToHexString(SHA256.HashData(stream)) == entry.Hash
                && SourceVersion.Read(Data(entry.Id, false)) == entry.LocalVersion
                && (!requireSource || SourceVersion.Read(entry.Source) == entry.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private void ValidateIndexAndDirectory()
    {
        if (entries.Count > 10000 || entries.Select(e => e.Id).Distinct().Count() != entries.Count
            || entries.Any(e => e.Id == Guid.Empty || e.Version.Length <= 0 || e.LastUse < 0
                || !Path.IsPathFullyQualified(e.Source) || InsideRoot(e.Source)
                || (e.Complete && (e.LocalVersion is null || e.LocalVersion.Length != e.Version.Length
                    || e.Hash.Length != 64 || !e.Hash.All(Uri.IsHexDigit)))))
            throw new IOException("Invalid cache index.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "owner.json", "store.lock", "index.json", "index.next" };
        foreach (var entry in entries)
        {
            allowed.Add(Path.GetFileName(Data(entry.Id, false)));
            if (!entry.Complete) allowed.Add(Path.GetFileName(Data(entry.Id, true)));
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            RequirePlainPath(path);
            if (Directory.Exists(path) || !allowed.Contains(Path.GetFileName(path)))
                throw new IOException("Unknown files in managed cache; preserved for inspection.");
        }
    }

    private void Delete(Entry entry)
    {
        if (pins.ContainsKey(entry.Id)) throw new InvalidOperationException("Cache entry is in use.");
        var pending = entry with { Complete = false };
        entries[entries.IndexOf(entry)] = pending;
        Save(); // Deletion intent survives a crash; never forget ownership before deleting.
        RequirePlainPath(Data(entry.Id, true));
        RequirePlainPath(Data(entry.Id, false));
        File.Delete(Data(entry.Id, true));
        File.Delete(Data(entry.Id, false));
        entries.Remove(pending);
        Save();
    }

    private void Save()
    {
        RequirePlainPath(root);
        RequirePlainPath(At("index.next"));
        RequirePlainPath(At("index.json"));
        using (var file = new FileStream(At("index.next"), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, entries);
            file.Flush(flushToDisk: true);
        }
        File.Move(At("index.next"), At("index.json"), overwrite: true);
    }

    private string At(string name) => Path.Combine(root, name);
    private string Data(Guid id, bool temporary) => At(id.ToString("N") + (temporary ? ".part" : ".media"));
    private bool InsideRoot(string path) => path.Equals(root, PathComparison)
        || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static void RequirePlainPath(string path)
    {
        for (var info = new FileInfo(Path.GetFullPath(path)); info is not null; info = info.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if ((File.Exists(info.FullName) || Directory.Exists(info.FullName) || info.LinkTarget is not null)
                && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                throw new IOException("Symbolic links and reparse points are unsupported.");
    }
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (copying || pins.Count != 0) throw new InvalidOperationException("Stop copies and release playback leases before closing.");
            disposed = true;
            ownership.Dispose();
        }
    }
}

public sealed class CacheLease : IDisposable
{
    private Action? release;
    public string Path { get; }
    public FileStream Stream { get; }
    internal CacheLease(string path, FileStream stream, Action release)
    { Path = path; Stream = stream; this.release = release; }
    public void Dispose()
    {
        var callback = Interlocked.Exchange(ref release, null);
        if (callback is null) return;
        try { Stream.Dispose(); } finally { callback(); }
    }
}
