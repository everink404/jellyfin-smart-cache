using JellyCache.Core;

var assertions = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    assertions++;
}
void Reject(Action action, string name)
{
    try { action(); } catch (ArgumentException) { assertions++; return; }
    throw new Exception(name);
}
var series = Guid.NewGuid();
var season = Guid.NewGuid();
var episodes = Enumerable.Range(1, 12).Select(n => new EpisodeInfo(Guid.NewGuid(), series,
    season, 1, n, $"/video/show/E{n}.mkv")).ToArray();
Check(PrefetchPlan.Window(episodes[0], episodes).Select(e => e.Number).SequenceEqual([2, 3]), "default ahead 2");
Check(PrefetchPlan.Window(episodes[7], episodes).Select(e => e.Number).SequenceEqual([9, 10]), "jump window");
Check(PrefetchPlan.Window(episodes[0], episodes, PrefetchStrategy.Next).Single().Number == 2, "next");
Check(PrefetchPlan.Window(episodes[7], episodes, PrefetchStrategy.Season).Count == 4, "remaining season");
Check(PrefetchPlan.Window(episodes[^1], episodes).Count == 0, "season end");
Check(PrefetchPlan.Window(episodes[0], episodes.Reverse().ToArray()).First().Number == 2, "metadata order");
var others = episodes.Concat([episodes[0] with { Id = Guid.NewGuid(), SeasonId = Guid.NewGuid(), Number = 2 }]).ToArray();
Check(PrefetchPlan.Window(episodes[0], others).Count == 2, "no cross season");
Check(PrefetchPlan.Window(episodes[0], [episodes[0], episodes[2], episodes[3]]).First().Number == 3, "known missing episode skipped");
Reject(() => PrefetchPlan.Window(episodes[0], episodes, ahead: 0), "invalid ahead");
Reject(() => PrefetchPlan.Window(episodes[0], episodes, (PrefetchStrategy)42), "invalid strategy");
Reject(() => PrefetchPlan.Window(episodes[0], episodes.Skip(1).ToArray()), "missing current");
Reject(() => PrefetchPlan.Window(episodes[0] with { SourcePath = "/changed" }, episodes), "current mismatch");
Reject(() => PrefetchPlan.Window(episodes[0], episodes.Concat([episodes[1]]).ToArray()), "duplicate identity");
Reject(() => PrefetchPlan.Window(episodes[0], [episodes[0], episodes[1] with { Number = 1 }]), "duplicate numbering");
Reject(() => PrefetchPlan.Window(episodes[0], [episodes[0], episodes[1] with { SourcePath = episodes[0].SourcePath }]), "duplicate source");
Reject(() => PrefetchPlan.Window(episodes[0], [episodes[0], episodes[1] with { EndNumber = 3 }]), "combined episode");
Reject(() => PrefetchPlan.Window(episodes[0] with { SeasonNumber = 0 }, episodes), "specials");
Reject(() => PrefetchPlan.Window(episodes[0], [episodes[0], episodes[1] with { SeasonNumber = 2 }]), "conflicting season");
var demand = new SessionDemand();
demand.Replace("a", episodes[0], episodes);
demand.Replace("a", episodes[0], episodes);
Check(demand.Snapshot().Count == 2, "repeat progress deduplicated");
demand.Replace("a", episodes[7], episodes);
Check(demand.Snapshot().Select(e => e.Episode.Number).SequenceEqual([9, 10]), "old window removed");
demand.Replace("b", episodes[7], episodes);
Check(demand.Snapshot().Count == 2 && demand.Snapshot()[0].Sessions.Count == 2, "shared file merged");
demand.Stop("a");
Check(demand.Snapshot()[0].Sessions.SequenceEqual(["b"]), "stop preserves other viewer");
Check(demand.Snapshot(new HashSet<Guid> { episodes[8].Id }).Single().Episode.Number == 10, "ready or inflight excluded");
Reject(() => demand.Replace("b", episodes[7], [episodes[0]]), "invalid refresh rejected");
Check(demand.Snapshot().Count == 0, "invalid refresh clears old demand");
demand.Replace("a", episodes[0], episodes);
demand.Replace("b", episodes[7], episodes);
Check(demand.Snapshot().Select(e => e.Episode.Number).SequenceEqual([2, 9, 3, 10]), "next episodes precede distant ones");
Check(demand.Snapshot(advance: true).First().Episode.Number == 2, "first scheduling turn");
Check(demand.Snapshot().First().Episode.Number == 9, "round robin");
Check(demand.Snapshot().First().Episode.Number == 9, "UI reads do not advance");
var conflicting = episodes.ToArray();
conflicting[1] = conflicting[1] with { SourcePath = "/changed/E2.mkv" };
demand.Replace("b", episodes[0], conflicting);
Check(demand.Snapshot().All(e => e.Episode.Id != episodes[1].Id), "conflicting version refused");
demand.Stop("a"); demand.Stop("b");
Check(demand.Snapshot().Count == 0, "all stopped");
Reject(() => demand.Stop("bad\nidentity"), "invalid session");
Parallel.For(0, 64, i => demand.Replace($"parallel{i}", episodes[0], episodes));
Check(demand.Snapshot().Count == 2 && demand.Snapshot()[0].Sessions.Count == 64, "concurrent sessions merged");
Parallel.For(0, 64, i => demand.Stop($"parallel{i}"));
Check(demand.Snapshot().Count == 0, "concurrent stops");
for (var i = 0; i < SessionDemand.MaximumSessions; i++) demand.Replace($"limit{i}", episodes[0], episodes);
try { demand.Replace("overflow", episodes[0], episodes); throw new Exception("session bound"); }
catch (InvalidOperationException) { assertions++; }
demand.Replace("limit0", episodes[7], episodes);
Check(demand.Snapshot().Any(e => e.Episode.Number == 9), "existing session updates at bound");
Console.WriteLine($"{assertions} core planning checks passed.");
await StorageChecks.Run();
await EngineChecks.Run();
