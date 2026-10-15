using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace JellyCache.ReadProbe;

// Diagnostic facts only. None of these observations authorize releasing a lease.
public sealed record ReadObservation(string Kind, bool HasPlaybackId, bool RangeRequested, string JobState)
{
    public static ReadObservation Capture(HttpContext? context, string cachePath)
    {
        try { return CaptureCore(context, cachePath); }
        catch { return new("unknown-request", false, false, "observation-unavailable"); }
    }

    private static ReadObservation CaptureCore(HttpContext? context, string cachePath)
    {
        if (context is null) return new("background-or-unknown", false, false, "not-observed");
        var controller = context.Request.RouteValues["controller"]?.ToString();
        var kind = controller switch
        {
            "MediaInfo" => "negotiation",
            "Videos" => "video-request",
            "DynamicHls" => "hls-request",
            "Subtitle" => "subtitle-request",
            _ => "unknown-request"
        };
        var ids = context.Request.Query["PlaySessionId"];
        var hasId = ids.Count == 1 && Guid.TryParse(ids[0], out _);
        var jobState = "not-observed";
        if (hasId && kind is "video-request" or "hls-request")
        {
            try
            {
                var manager = context.RequestServices.GetService<ITranscodeManager>();
                jobState = manager is null ? "manager-unavailable" : Classify(manager.GetTranscodingJob(ids[0]!), cachePath);
            }
            catch { jobState = "observation-unavailable"; }
        }
        return new(kind, hasId, context.Request.Headers.ContainsKey("Range"), jobState);
    }

    public static string Classify(TranscodingJob? job, string cachePath)
    {
        if (job is null) return "no-job-observed";
        if (!string.Equals(job.MediaSource?.Path, cachePath, StringComparison.Ordinal)) return "different-or-unknown-input";
        return job.HasExited ? "matching-job-exited" : "matching-job-not-exited";
    }
}

public sealed record ProtectionObservation(Guid Id, string FirstStage, string LastStage, long Selections,
    ReadObservation Request, string ReleasePolicy = "until-host-stops");
