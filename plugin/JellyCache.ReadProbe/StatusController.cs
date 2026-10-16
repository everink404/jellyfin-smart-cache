using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JellyCache.ReadProbe;

[ApiController]
[Route("JellyCacheReadProbe")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class StatusController(ProbeState state, PrefetchHost prefetch) : ControllerBase
{
    [HttpGet("Status")]
    public object Status() => state.Snapshot();
    [HttpGet("PrefetchStatus")]
    public object PrefetchStatus() => prefetch.Snapshot();
    [HttpPost("ClearUnoccupied")]
    public Task<JellyCache.Core.CacheClearResult> ClearUnoccupied() => prefetch.ClearUnoccupiedAsync();
}
