using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;

namespace JellyCache.ReadProbe;

internal sealed class RawOwner<T>(T value) : IDisposable
{
    public T Value { get; } = value;
    public void Dispose() => (Value as IDisposable)?.Dispose();
}

internal static class EncodingRegistration
{
    private static bool Known<T>(IServiceCollection services, string type) =>
        services.Count(d => d.ServiceType == typeof(T)) == 1 && services.Any(d => d.ServiceType == typeof(T)
            && d.Lifetime == ServiceLifetime.Singleton && d.ImplementationType?.FullName == type);
    public static bool CanWrap(IServiceCollection services) =>
        Known<ITranscodeManager>(services, "MediaBrowser.MediaEncoding.Transcoding.TranscodeManager")
        && Known<ISubtitleEncoder>(services, "MediaBrowser.MediaEncoding.Subtitles.SubtitleEncoder")
        && Known<IAttachmentExtractor>(services, "MediaBrowser.MediaEncoding.Attachments.AttachmentExtractor");
    public static void Wrap(IServiceCollection services)
    {
        Decorate<ITranscodeManager>(services, (inner, access, requests) => new ProtectedTranscodeManager(inner, access, requests));
        Decorate<ISubtitleEncoder>(services, (inner, access, _) => new ProtectedSubtitleEncoder(inner, access));
        Decorate<IAttachmentExtractor>(services, (inner, access, _) => new ProtectedAttachmentExtractor(inner, access));
    }
    private static void Decorate<T>(IServiceCollection services, Func<T, PrefetchAccess, IHttpContextAccessor?, T> factory) where T : class
    {
        var original = services.Single(d => d.ServiceType == typeof(T));
        services.Remove(original);
        services.AddSingleton(sp => new RawOwner<T>((T)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)));
        services.AddSingleton<T>(sp => factory(sp.GetRequiredService<RawOwner<T>>().Value, sp.GetRequiredService<PrefetchAccess>(), sp.GetService<IHttpContextAccessor>()));
    }
}

internal static class ProtectedOperation
{
    public static async Task<T> Run<T>(PrefetchAccess access, Func<Task<T>> action, CancellationToken token, params string?[] paths)
    {
        var host = access.Host;
        if (host is null) return await action().ConfigureAwait(false);
        var scope = new ReadLeaseScope();
        var previous = ReadLeaseScope.Operation.Value;
        ReadLeaseScope.Operation.Value = scope;
        try
        {
            foreach (var path in paths) host.PinPath(scope, path);
            var result = await action().ConfigureAwait(false);
            // Native process cancellation may have requested termination without joining.
            if (token.IsCancellationRequested) host.RetainUnknown(scope);
            else scope.Dispose();
            return result;
        }
        catch { host.RetainUnknown(scope); throw; }
        finally { ReadLeaseScope.Operation.Value = previous; host.FinishedOperation(); }
    }
    public static Task Run(PrefetchAccess access, Func<Task> action, CancellationToken token, params string?[] paths)
        => Run(access, async () => { await action().ConfigureAwait(false); return true; }, token, paths);
}

public sealed class ProtectedSubtitleEncoder(ISubtitleEncoder inner, PrefetchAccess access) : ISubtitleEncoder
{
    public Task<Stream> GetSubtitles(BaseItem item, string sourceId, int index, string format, long start, long end, bool timestamps, CancellationToken token)
        => ProtectedOperation.Run(access, () => inner.GetSubtitles(item, sourceId, index, format, start, end, timestamps, token), token);
    public Task<string> GetSubtitleFileCharacterSet(MediaStream stream, string language, MediaSourceInfo source, CancellationToken token)
        => ProtectedOperation.Run(access, () => inner.GetSubtitleFileCharacterSet(stream, language, source, token), token, source.Path);
    public Task<string> GetSubtitleFilePath(MediaStream stream, MediaSourceInfo source, CancellationToken token)
        => ProtectedOperation.Run(access, () => inner.GetSubtitleFilePath(stream, source, token), token, source.Path);
    public Task ExtractAllExtractableSubtitles(MediaSourceInfo source, CancellationToken token)
        => ProtectedOperation.Run(access, () => inner.ExtractAllExtractableSubtitles(source, token), token, source.Path);
}

public sealed class ProtectedAttachmentExtractor(IAttachmentExtractor inner, PrefetchAccess access) : IAttachmentExtractor
{
    public Task<(MediaAttachment Attachment, Stream Stream)> GetAttachment(BaseItem item, string sourceId, int index, CancellationToken token)
        => ProtectedOperation.Run(access, () => inner.GetAttachment(item, sourceId, index, token), token);
    public Task ExtractAllAttachments(string input, MediaSourceInfo source, CancellationToken token)
        => ProtectedOperation.Run(access, () => inner.ExtractAllAttachments(input, source, token), token, input, source.Path);
}

public sealed class ProtectedTranscodeManager(ITranscodeManager inner, PrefetchAccess access, IHttpContextAccessor? requests = null) : ITranscodeManager
{
    public async Task<TranscodingJob> StartFfMpeg(StreamState state, string output, string arguments, Guid user,
        TranscodingJobType type, CancellationTokenSource cancellation, string? directory = null)
    {
        var host = access.Host;
        if (host is null) return await inner.StartFfMpeg(state, output, arguments, user, type, cancellation, directory).ConfigureAwait(false);
        var scope = new ReadLeaseScope();
        var previous = ReadLeaseScope.Operation.Value;
        ReadLeaseScope.Operation.Value = scope;
        try
        {
            host.PinPath(scope, state.MediaPath);
            host.PinPath(scope, state.MediaSource?.Path);
            var job = await inner.StartFfMpeg(state, output, arguments, user, type, cancellation, directory).ConfigureAwait(false);
            _ = ObserveExit(host, scope, job);
            return job;
        }
        catch
        {
            // Start may fail after launching a process. Only a positively identified exit permits release.
            TranscodingJob? job = null;
            try { job = inner.GetTranscodingJob(output, type); } catch { }
            if (job is not null) _ = ObserveExit(host, scope, job);
            else host.RetainUnknown(scope);
            throw;
        }
        finally { ReadLeaseScope.Operation.Value = previous; }
    }
    private static async Task ObserveExit(PrefetchHost host, ReadLeaseScope scope, TranscodingJob job)
    {
        try
        {
            if (!job.HasExited)
            {
                if (job.Process is null) { host.RetainUnknown(scope); return; }
                await job.Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            scope.Dispose();
        }
        catch
        {
            if (job.HasExited) scope.Dispose();
            else host.RetainUnknown(scope);
        }
        finally { host.FinishedOperation(); }
    }
    public TranscodingJob? GetTranscodingJob(string id)
    {
        var job = inner.GetTranscodingJob(id);
        var host = access.Host;
        if (job is null || host is null || !host.IsSelectedCache(job.MediaSource?.Path)) return job;
        // The core may retain exited job descriptors after their input has been reclaimed.
        // Make StreamingHelpers resolve a fresh source instead of reopening that stale path.
        if (job.HasExited) return null;
        var scope = ReadLeaseScope.Operation.Value ?? requests?.HttpContext?.Features.Get<ReadLeaseScope>();
        if (scope is not null)
        {
            try { host.PinPath(scope, job.MediaSource?.Path); }
            catch (IOException) { return null; }
        }
        return job;
    }
    public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type) => inner.GetTranscodingJob(path, type);
    public void PingTranscodingJob(string id, bool? paused) => inner.PingTranscodingJob(id, paused);
    public Task KillTranscodingJobs(string device, string? session, Func<string, bool> delete) => inner.KillTranscodingJobs(device, session, delete);
    public void ReportTranscodingProgress(TranscodingJob job, StreamState state, TimeSpan? position, float? fps, double? complete, long? bytes, int? bitrate)
        => inner.ReportTranscodingProgress(job, state, position, fps, complete, bytes, bitrate);
    public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type) => inner.OnTranscodeBeginRequest(path, type);
    public void OnTranscodeEndRequest(TranscodingJob job) => inner.OnTranscodeEndRequest(job);
    public ValueTask<IDisposable> LockAsync(string path, CancellationToken token) => inner.LockAsync(path, token);
}
