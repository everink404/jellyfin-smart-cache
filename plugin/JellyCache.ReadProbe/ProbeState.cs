using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyCache.ReadProbe;

public sealed record ProbeSettings(bool Enabled, string Source, string Cache, int Revision)
{
    public static ProbeSettings Current()
    {
        var c = Plugin.Instance?.Configuration;
        return c is null ? new(false, "", "", 0) : new(c.Enabled, c.SourcePath, c.CachePath, c.ValidationRevision);
    }
}

public sealed record FileStamp(long Length, long Mtime)
{
    public static FileStamp Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.LinkTarget is not null) throw new IOException("invalid-file");
        return new(info.Length, info.LastWriteTimeUtc.Ticks);
    }
}

public sealed record VerifiedCopy(ProbeSettings Settings, FileStamp Source, FileStamp Cache);

public sealed class ProbeState : BackgroundService
{
    private readonly ILogger<ProbeState> logger;
    private readonly RegistrationResult registration;
    private readonly Func<ProbeSettings> settings;
    private VerifiedCopy? verified;
    private string status = "disabled";
    private long hits, fallbacks;
    public ProbeState(ILogger<ProbeState> logger, RegistrationResult registration)
        : this(logger, registration, ProbeSettings.Current) { }
    public ProbeState(ILogger<ProbeState> logger, RegistrationResult registration, Func<ProbeSettings> settings)
    { this.logger = logger; this.registration = registration; this.settings = settings; }
    public object Snapshot() => new { Registered = registration.Supported, State = Volatile.Read(ref status),
        CacheSelections = Interlocked.Read(ref hits), FallbackSelections = Interlocked.Read(ref fallbacks) };

    private void SetStatus(string value)
    {
        if (Interlocked.Exchange(ref status, value) != value)
            logger.LogInformation("JellyCache read test state: {State}", value);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("JellyCache read test loaded; routing registration: {Supported}", registration.Supported);
        ProbeSettings? previous = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = settings();
            if (current != previous)
            {
                previous = current;
                await ValidateAsync(current, stoppingToken).ConfigureAwait(false);
            }
            await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task ValidateAsync(ProbeSettings current, CancellationToken cancellationToken)
    {
        Volatile.Write(ref verified, null);
        if (!registration.Supported) { SetStatus("unsupported-registration"); return; }
        if (!current.Enabled) { SetStatus("disabled"); return; }
        try
        {
            if (!Path.IsPathFullyQualified(current.Source) || !Path.IsPathFullyQualified(current.Cache)
                || string.Equals(Path.GetFullPath(current.Source), Path.GetFullPath(current.Cache),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            { SetStatus("invalid-paths"); return; }
            SetStatus("verifying");
            var originStamp = FileStamp.Read(current.Source);
            var cacheStamp = FileStamp.Read(current.Cache);
            if (originStamp.Length != cacheStamp.Length) { SetStatus("size-mismatch"); return; }
            await using var source = new FileStream(current.Source, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var cache = new FileStream(current.Cache, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var originHash = await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false);
            var cacheHash = await SHA256.HashDataAsync(cache, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(originHash, cacheHash)) { SetStatus("hash-mismatch"); return; }
            if (originStamp != FileStamp.Read(current.Source) || cacheStamp != FileStamp.Read(current.Cache)
                || current != settings()) { SetStatus("changed-during-verification"); return; }
            Volatile.Write(ref verified, new(current, originStamp, cacheStamp));
            SetStatus("ready");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { SetStatus("file-unavailable"); }
    }

    public IReadOnlyList<MediaSourceInfo> Select(BaseItem item, IReadOnlyList<MediaSourceInfo> sources, string stage)
    {
        var current = settings();
        if (!current.Enabled || item is not Episode || item.Path != current.Source) return sources;
        var copy = Volatile.Read(ref verified);
        try
        {
            if (sources.Count != 1 || sources[0].Protocol != MediaProtocol.File || sources[0].Path != current.Source
                || copy is null || copy.Settings != current)
                return Fallback(sources, stage, "not-ready-or-unsupported-source");
            if (copy.Source != FileStamp.Read(current.Source) || copy.Cache != FileStamp.Read(current.Cache))
            {
                Volatile.Write(ref verified, null);
                SetStatus("file-changed-save-to-reverify");
                return Fallback(sources, stage, "file-changed");
            }
            var selected = JsonSerializer.Deserialize<MediaSourceInfo>(
                JsonSerializer.Serialize(sources[0], JsonDefaults.Options), JsonDefaults.Options)!;
            selected.Path = current.Cache;
            selected.EncoderPath = null;
            selected.EncoderProtocol = null;
            Interlocked.Increment(ref hits);
            logger.LogInformation("JellyCache read test selection: cache; stage={Stage}; item={Item}", stage, item.Id);
            return [selected];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        {
            Volatile.Write(ref verified, null);
            SetStatus("file-unavailable");
            return Fallback(sources, stage, "file-unavailable");
        }
    }
    private IReadOnlyList<MediaSourceInfo> Fallback(IReadOnlyList<MediaSourceInfo> sources, string stage, string reason)
    {
        Interlocked.Increment(ref fallbacks);
        logger.LogInformation("JellyCache read test selection: origin; stage={Stage}; reason={Reason}", stage, reason);
        return sources;
    }
}
