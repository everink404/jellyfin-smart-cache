using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using JellyCache.Core;
using JellyCache.ReadProbe;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class HttpReadChecks
{
    public static async Task Run()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var root = Path.GetFullPath(Path.Combine(".tools", "http-read-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.bin");
        var replacement = Path.Combine(root, "replacement.bin");
        const int size = 32 * 1024 * 1024;
        var block = Enumerable.Range(0, 256 * 1024).Select(i => (byte)(i % 251)).ToArray();
        foreach (var path in new[] { source, replacement })
        {
            await using var output = File.Create(path);
            for (var offset = 0; offset < size; offset += block.Length) await output.WriteAsync(block);
        }
        var id = Guid.NewGuid(); var other = Guid.NewGuid();
        using var store = new CacheStore(Path.Combine(root, "managed"), new(size, 0, 1024L * 1024 * 1024));
        await store.CopyAsync(id, source);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxResponseBufferSize = 32 * 1024;
        });
        builder.Services.AddControllers();
        builder.Services.AddSingleton<IStartupFilter, ReadLeaseStartupFilter>();
        await using var app = builder.Build();
        var released = new ConcurrentDictionary<string, TaskCompletionSource>();
        var arrived = new ConcurrentDictionary<string, TaskCompletionSource>();
        var gates = new ConcurrentDictionary<string, TaskCompletionSource>();
        TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobLeases = new BackgroundReadLeases();
        var workIdentity = new object();
        var workStarted = Signal(); var workFinish = Signal(); var workRequestClosed = Signal();
        Task<byte[]>? work = null;
        foreach (var mode in new[] { "full", "range", "invalid-range", "abort", "abort-before-open", "head", "parallel-a", "parallel-b", "failure" })
        { released[mode] = Signal(); arrived[mode] = Signal(); gates[mode] = Signal(); }
        app.MapMethods("/file/{mode}", ["GET", "HEAD"], async context =>
        {
            var mode = (string)context.Request.RouteValues["mode"]!;
            var scope = context.Features.Get<ReadLeaseScope>() ?? throw new Exception("Startup filter did not install request scope");
            scope.Completion.GetAwaiter().OnCompleted(() => released[mode].TrySetResult());
            var lease = scope.TryAcquire(store, id, source) ?? throw new Exception("Expected managed cache hit");
            try
            {
                arrived[mode].TrySetResult();
                if (mode == "failure") throw new InvalidOperationException("Synthetic endpoint failure.");
                if (mode is "full" or "abort-before-open" or "parallel-a" or "parallel-b")
                    await gates[mode].Task.WaitAsync(context.RequestAborted);
                // Same result type and range option as the pinned Jellyfin helper.
                var result = new PhysicalFileResult(lease.Path, "application/octet-stream") { EnableRangeProcessing = true };
                await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        app.MapGet("/background-read", async context =>
        {
            var scope = context.Features.Get<ReadLeaseScope>() ?? throw new Exception("Missing request scope");
            scope.Completion.GetAwaiter().OnCompleted(() => workRequestClosed.TrySetResult());
            var lease = scope.TryAcquire(store, id, source) ?? throw new Exception("Expected cache hit");
            if (!scope.ProtectJob(jobLeases, workIdentity, id)) throw new Exception("Background association failed");
            work = Task.Run(async () =>
            {
                workStarted.TrySetResult();
                await workFinish.Task;
                await using var input = File.OpenRead(lease.Path);
                var bytes = new byte[1024];
                await input.ReadExactlyAsync(bytes);
                return bytes;
            });
            await workStarted.Task;
            await context.Response.WriteAsync("started");
        });
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var handler = new SocketsHttpHandler { UseProxy = false };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            async Task Done(string mode) => await released[mode].Task.WaitAsync(TimeSpan.FromSeconds(10));

            var fullTask = client.GetAsync("/file/full");
            if (await Task.WhenAny(arrived["full"].Task, fullTask, Task.Delay(TimeSpan.FromSeconds(10))) != arrived["full"].Task)
            {
                if (fullTask.IsCompleted)
                {
                    using var failed = await fullTask;
                    throw new Exception($"Loopback request did not acquire lease: HTTP {(int)failed.StatusCode}");
                }
                throw new TimeoutException("Loopback request did not reach fixture endpoint.");
            }
            Check(store.Usage.Leases == 1, "lease covers selected path before physical-file executor opens it");
            Check(await store.CopyAsync(other, replacement) == CopyOutcome.CapacityBlocked, "capacity pressure cannot evict pre-open response source");
            gates["full"].TrySetResult();
            using (var response = await fullTask)
            {
                var data = await response.Content.ReadAsByteArrayAsync();
                Check(response.StatusCode == HttpStatusCode.OK && data.Length == size
                    && data.AsSpan(100, 1000).SequenceEqual(block.AsSpan(100, 1000)), "real full HTTP response returns cached bytes");
            }
            await Done("full");
            Check(store.Usage.Leases == 0, "full physical-file execution releases request lease");

            using (var requestA = new HttpRequestMessage(HttpMethod.Get, "/file/parallel-a"))
            using (var requestB = new HttpRequestMessage(HttpMethod.Get, "/file/parallel-b"))
            {
                requestA.Headers.Range = new RangeHeaderValue(0, 1023);
                requestB.Headers.Range = new RangeHeaderValue(1024, 2047);
                var responseA = client.SendAsync(requestA);
                var responseB = client.SendAsync(requestB);
                await Task.WhenAll(arrived["parallel-a"].Task, arrived["parallel-b"].Task).WaitAsync(TimeSpan.FromSeconds(10));
                Check(store.Usage.Leases == 2, "two real clients independently pin same cached file");
                gates["parallel-a"].TrySetResult();
                using (var a = await responseA)
                    Check(a.StatusCode == HttpStatusCode.PartialContent, "first concurrent range completes");
                await Done("parallel-a");
                Check(store.Usage.Leases == 1 && await store.CopyAsync(other, replacement) == CopyOutcome.CapacityBlocked,
                    "first client completion cannot evict second client's source");
                gates["parallel-b"].TrySetResult();
                using (var b = await responseB)
                    Check((await b.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(block.AsSpan(1024, 1024)), "second concurrent client receives intact bytes");
                await Done("parallel-b");
                Check(store.Usage.Leases == 0, "all client completions release all request leases");
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, "/file/range"))
            {
                request.Headers.Range = new RangeHeaderValue(700, 1699);
                using var response = await client.SendAsync(request);
                var data = await response.Content.ReadAsByteArrayAsync();
                Check(response.StatusCode == HttpStatusCode.PartialContent && data.AsSpan().SequenceEqual(block.AsSpan(700, 1000)), "real HTTP Range returns exact seek bytes");
            }
            await Done("range");
            Check(store.Usage.Leases == 0, "range execution releases its own lease");

            using (var request = new HttpRequestMessage(HttpMethod.Get, "/file/invalid-range"))
            {
                request.Headers.Range = new RangeHeaderValue(size + 1L, null);
                using var response = await client.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable, "invalid range handled by real executor");
            }
            await Done("invalid-range");
            Check(store.Usage.Leases == 0, "rejected range leaves no lease");

            using (var request = new HttpRequestMessage(HttpMethod.Head, "/file/head"))
            using (var response = await client.SendAsync(request))
                Check(response.Content.Headers.ContentLength == size && (await response.Content.ReadAsByteArrayAsync()).Length == 0,
                    "HEAD returns size without response data");
            await Done("head");
            Check(store.Usage.Leases == 0, "HEAD leaves no lease");
            using (var response = await client.GetAsync("/file/failure"))
                Check(response.StatusCode == HttpStatusCode.InternalServerError, "endpoint failure traverses real host error path");
            await Done("failure");
            Check(store.Usage.Leases == 0, "middleware unwinds request lease when endpoint throws");

            using (var cancellation = new CancellationTokenSource())
            {
                var pending = client.GetAsync("/file/abort-before-open", cancellation.Token);
                await arrived["abort-before-open"].Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancellation.Cancel();
                try { await pending; throw new Exception("Expected cancelled HTTP request"); }
                catch (OperationCanceledException) { }
                await Done("abort-before-open");
                Check(store.Usage.Leases == 0, "disconnect before executor opens source releases transition lease");
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, "/file/abort"))
            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
            {
                await using var stream = await response.Content.ReadAsStreamAsync();
                var first = new byte[1024];
                Check(await stream.ReadAsync(first) > 0, "disconnect test starts actual file transmission");
                await Task.Delay(100);
                Check(!released["abort"].Task.IsCompleted && store.Usage.Leases == 1, "slow client keeps lease while physical-file send is blocked");
            }
            await Done("abort");
            Check(store.Usage.Leases == 0, "mid-response disconnect unwinds real executor and releases lease");
            Check(await client.GetStringAsync("/background-read") == "started", "background file reader starts before request completes");
            await workRequestClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(store.Usage.Leases == 1 && !work!.IsCompleted && await store.CopyAsync(other, replacement) == CopyOutcome.CapacityBlocked,
                "HTTP scope closes before background file open without dropping its pin");
            workFinish.TrySetResult();
            var readBytes = await work!.WaitAsync(TimeSpan.FromSeconds(10));
            Check(readBytes.AsSpan().SequenceEqual(block.AsSpan(0, 1024)), "background reader receives intact cached bytes after HTTP completion");
            jobLeases.ReleaseAfterWork(workIdentity);
            Check(store.Usage.Leases == 0, "joined background reader releases last pin");
            Check(await store.CopyAsync(other, replacement) == CopyOutcome.Published && !store.IsReady(id, source),
                "completed request allows safe LRU reclaim under capacity pressure");
        }
        finally
        {
            workFinish.TrySetResult();
            try { if (work is not null) await work; }
            finally { jobLeases.ReleaseAfterWork(workIdentity); await app.StopAsync(); }
        }
        Console.WriteLine($"{count} real loopback HTTP file-response checks passed. Kestrel/MVC and managed cache; no Jellyfin server, ffmpeg, NAS or production lease integration.");
    }
}
