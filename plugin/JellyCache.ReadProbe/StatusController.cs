using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JellyCache.ReadProbe;

[ApiController]
[Route("JellyCacheReadProbe")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class StatusController(ProbeState state) : ControllerBase
{
    [HttpGet("Status")]
    public object Status() => state.Snapshot();
}
