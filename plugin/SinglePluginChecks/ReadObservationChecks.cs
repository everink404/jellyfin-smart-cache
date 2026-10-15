using JellyCache.ReadProbe;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

internal static class ReadObservationChecks
{
    public static async Task Run()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        const string cache = "/cache/fixture.media";
        using var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        { PlaySessionId = Guid.NewGuid().ToString("N"), MediaSource = new MediaSourceInfo { Path = cache }, ActiveRequestCount = 0 };
        var manager = DispatchProxy.Create<ITranscodeManager, ObservationProxy>();
        var queries = 0;
        ((ObservationProxy)(object)manager).Handler = (m, args) =>
        {
            if (m.Name != "GetTranscodingJob") throw new Exception("Observation must not mutate transcode manager");
            queries++;
            return (string)args[0]! == job.PlaySessionId ? job : null;
        };
        using var services = new ServiceCollection().AddSingleton(manager).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.RouteValues["controller"] = "Videos";
        context.Request.QueryString = new("?PlaySessionId=" + job.PlaySessionId);
        context.Request.Headers.Range = "bytes=100-200";
        var observation = ReadObservation.Capture(context, cache);
        Check(observation is { Kind: "video-request", HasPlaybackId: true, RangeRequested: true, JobState: "matching-job-not-exited" }, "range request linked to matching official job descriptor");
        Check(job.ActiveRequestCount == 0 && !job.HasExited, "zero HTTP requests does not prove ffmpeg exit");
        job.HasExited = true;
        Check(ReadObservation.Capture(context, cache).JobState == "matching-job-exited", "exit fact observed separately");
        Check(ReadObservation.Capture(context, "/cache/other.media").JobState == "different-or-unknown-input", "old or replaced input is not linked by session ID alone");
        context.Request.QueryString = new("?PlaySessionId=" + Guid.NewGuid().ToString("N"));
        Check(ReadObservation.Capture(context, cache).JobState == "no-job-observed", "new seek session can precede job creation");
        var before = queries;
        context.Request.QueryString = new("?PlaySessionId=untrusted&api_key=never-report");
        context.Request.RouteValues["controller"] = "Subtitle";
        Check(ReadObservation.Capture(context, cache) is { Kind: "subtitle-request", HasPlaybackId: false }
            && queries == before, "invalid playback identifier never queried or reported");
        context.Request.QueryString = new("?PlaySessionId=" + job.PlaySessionId + "&PlaySessionId=" + job.PlaySessionId);
        Check(!ReadObservation.Capture(context, cache).HasPlaybackId, "ambiguous playback ID rejected");
        Check(ReadObservation.Capture(null, cache).Kind == "background-or-unknown", "background subtitle or metadata work remains unknown");
        context.Request.QueryString = new("?PlaySessionId=" + job.PlaySessionId);
        context.Request.RouteValues["controller"] = "MediaInfo";
        before = queries;
        Check(ReadObservation.Capture(context, cache).Kind == "negotiation" && queries == before, "pure negotiation does not resolve a transcode manager");
        context.Request.RouteValues["controller"] = "Videos";
        ((ObservationProxy)(object)manager).Handler = (_, _) => throw new IOException("private source path and credentials must not escape");
        var failedObservation = ReadObservation.Capture(context, cache);
        Check(failedObservation.JobState == "observation-unavailable", "observer failure isolated from cache routing");
        Check(!System.Text.Json.JsonSerializer.Serialize(failedObservation).Contains(job.PlaySessionId!)
            && !System.Text.Json.JsonSerializer.Serialize(failedObservation).Contains("credentials"), "diagnostic exports no raw playback ID, query or exception message");
        var accessor = new HttpContextAccessor { HttpContext = context };
        var other = new DefaultHttpContext();
        await Task.Run(() => { accessor.HttpContext = other; Check(accessor.HttpContext == other, "child async flow gets its own request"); });
        // ASP.NET deliberately clears the shared holder when switching requests.
        Check(accessor.HttpContext != other, "child request does not leak into parent flow");
        accessor.HttpContext = null;
        Console.WriteLine($"{count} request/job observation checks passed. Synthetic HttpContext and job descriptors; no actual ffmpeg or HTTP response execution.");
    }
}

public class ObservationProxy : DispatchProxy
{
    public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args ?? []);
}
