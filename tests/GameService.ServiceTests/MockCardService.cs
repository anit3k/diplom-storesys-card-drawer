using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameService.ServiceTests;

/// <summary>What the mock received for one call to /cards/random.</summary>
internal sealed record RecordedCall(DateTimeOffset At, string Path, string? IdempotencyKey, string? Traceparent);

/// <summary>
/// A hand-made "microservice mock" of CardService (book, ch. 8): a real Kestrel minimal-API server
/// on an ephemeral loopback port. Its per-call behaviour is scriptable and can be reprogrammed mid
/// test; every call is recorded thread-safely. Owned and disposed by the test.
/// </summary>
internal sealed class MockCardService : IAsyncDisposable
{
    internal const string CardJson = """{"suit":"Hearts","rank":"Queen","isJoker":false}""";

    private readonly WebApplication app;
    private readonly ConcurrentQueue<RecordedCall> calls = new();
    private int callCounter;

    // The responder: (callNumber starting at 1, context) -> writes the response. volatile so a test
    // can swap it from another thread and the server picks it up immediately.
    private volatile Func<int, HttpContext, Task> responder;

    private MockCardService(WebApplication app, Func<int, HttpContext, Task> responder)
    {
        this.app = app;
        this.responder = responder;
    }

    public string BaseUrl { get; private set; } = string.Empty;

    public int CallCount => this.calls.Count;

    public IReadOnlyList<RecordedCall> Calls => this.calls.ToArray();

    public void Reprogram(Func<int, HttpContext, Task> responder) => this.responder = responder;

    public static async Task<MockCardService> StartAsync(Func<int, HttpContext, Task> responder)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // keep test output clean
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // ephemeral loopback port

        var app = builder.Build();
        var mock = new MockCardService(app, responder);

        app.MapGet("/cards/random", async (HttpContext context) =>
        {
            var n = Interlocked.Increment(ref mock.callCounter);
            mock.calls.Enqueue(new RecordedCall(
                DateTimeOffset.UtcNow,
                context.Request.Path,
                context.Request.Headers.TryGetValue("Idempotency-Key", out var k) ? k.ToString() : null,
                context.Request.Headers.TryGetValue("traceparent", out var t) ? t.ToString() : null));

            await mock.responder(n, context);
        });

        await app.StartAsync();
        mock.BaseUrl = app.Urls.First(); // e.g. http://127.0.0.1:49999
        return mock;
    }

    public async ValueTask DisposeAsync() => await this.app.DisposeAsync();

    // ---- responder builders ----

    /// <summary>Always returns a card (200).</summary>
    public static Func<int, HttpContext, Task> AlwaysCard() =>
        (_, ctx) => WriteCard(ctx);

    /// <summary>Always returns the given status code (no body).</summary>
    public static Func<int, HttpContext, Task> AlwaysStatus(int statusCode) =>
        (_, ctx) => { ctx.Response.StatusCode = statusCode; return Task.CompletedTask; };

    /// <summary>Fails with <paramref name="status"/> for the first <paramref name="failCount"/> calls, then a card.</summary>
    public static Func<int, HttpContext, Task> FailThenCard(int failCount, int status = 503) =>
        (n, ctx) =>
        {
            if (n <= failCount) { ctx.Response.StatusCode = status; return Task.CompletedTask; }
            return WriteCard(ctx);
        };

    /// <summary>Delays (respecting cancellation) then returns a card — used to trigger timeouts.</summary>
    public static Func<int, HttpContext, Task> Delay(TimeSpan delay) =>
        async (_, ctx) =>
        {
            try { await Task.Delay(delay, ctx.RequestAborted); }
            catch (OperationCanceledException) { return; }
            await WriteCard(ctx);
        };

    /// <summary>Slow for the first call, then a card.</summary>
    public static Func<int, HttpContext, Task> SlowThenCard(TimeSpan delay) =>
        async (n, ctx) =>
        {
            if (n == 1)
            {
                try { await Task.Delay(delay, ctx.RequestAborted); }
                catch (OperationCanceledException) { return; }
            }

            await WriteCard(ctx);
        };

    private static async Task WriteCard(HttpContext ctx)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(CardJson);
    }
}
