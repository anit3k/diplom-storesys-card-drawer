using System.Net;
using System.Text;
using GameService;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GameService.UnitTests;

internal delegate Task<HttpResponseMessage> ScriptedAction(HttpRequestMessage request, CancellationToken cancellationToken);

/// <summary>What the fake CardService received for one call.</summary>
internal sealed record RecordedCall(HttpMethod Method, Uri? Uri, string? IdempotencyKey, string? Traceparent);

/// <summary>
/// Hand-made primary HTTP handler standing in for CardService. Plays a queue of scripted actions
/// (status/body, throw, delay that respects cancellation); the last action repeats once the queue
/// is drained (so "fails twice then succeeds" is [fail, fail, ok] and "always fails" is one action).
/// Records every call it received.
/// </summary>
internal sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<ScriptedAction> actions;

    public ScriptedHttpMessageHandler(params ScriptedAction[] actions)
    {
        if (actions.Length == 0)
        {
            throw new ArgumentException("At least one action is required.", nameof(actions));
        }

        this.actions = new Queue<ScriptedAction>(actions);
    }

    public List<RecordedCall> Calls { get; } = new();

    public int CallCount => this.Calls.Count;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.Headers.TryGetValues("Idempotency-Key", out var k) ? string.Join(",", k) : null;
        var tp = request.Headers.TryGetValues("traceparent", out var t) ? string.Join(",", t) : null;
        lock (this.Calls)
        {
            this.Calls.Add(new RecordedCall(request.Method, request.RequestUri, key, tp));
        }

        var action = this.actions.Count > 1 ? this.actions.Dequeue() : this.actions.Peek();
        return await action(request, cancellationToken);
    }

    // ---- action builders ----

    public static ScriptedAction Respond(HttpStatusCode code, string? json = null) =>
        (_, _) => Task.FromResult(new HttpResponseMessage(code)
        {
            Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json"),
        });

    public static ScriptedAction OrdinaryCard(string suit = "Hearts", string rank = "Queen") =>
        Respond(HttpStatusCode.OK, $$"""{"suit":"{{suit}}","rank":"{{rank}}","isJoker":false}""");

    public static ScriptedAction Joker() =>
        Respond(HttpStatusCode.OK, """{"suit":null,"rank":null,"isJoker":true}""");

    public static ScriptedAction Throw(Exception exception) =>
        (_, _) => throw exception;

    public static ScriptedAction DelayThenOk(TimeSpan delay) =>
        async (_, cancellationToken) =>
        {
            await Task.Delay(delay, cancellationToken); // respects cancellation (attempt timeout)
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"suit":"Hearts","rank":"Queen","isJoker":false}""", Encoding.UTF8, "application/json"),
            };
        };
}

/// <summary>
/// Runs GameService in-process with small, fast resilience values (real time, big margins) and the
/// CardService typed client's PRIMARY handler swapped for a <see cref="ScriptedHttpMessageHandler"/>.
/// The whole real pipeline (timeouts, retry, breaker), the client translation and the central
/// exception handler still run. Each test news up its own instance (no shared state).
/// </summary>
internal sealed class GameServiceApp : WebApplicationFactory<Program>
{
    private readonly ScriptedHttpMessageHandler handler;
    private readonly Dictionary<string, string?> config;

    public GameServiceApp(ScriptedHttpMessageHandler handler, IDictionary<string, string?>? configOverrides = null)
    {
        this.handler = handler;
        this.config = new Dictionary<string, string?>
        {
            ["CardService:BaseUrl"] = "http://cardservice.test",
            ["CardService:Resilience:AttemptTimeout"] = "00:00:00.100",
            ["CardService:Resilience:TotalTimeout"] = "00:00:01",
            ["CardService:Resilience:MaxRetryAttempts"] = "2",
            ["CardService:Resilience:RetryBaseDelay"] = "00:00:00.010",
            ["CardService:Resilience:UseJitter"] = "false",
            // Breaker set so it never trips during the retry tests (tested in part 3).
            ["CardService:Resilience:FailureRatio"] = "0.5",
            ["CardService:Resilience:MinimumThroughput"] = "100",
            ["CardService:Resilience:SamplingDuration"] = "00:00:30",
            ["CardService:Resilience:BreakDuration"] = "00:00:05",
        };

        if (configOverrides is not null)
        {
            foreach (var (key, value) in configOverrides)
            {
                this.config[key] = value;
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(this.config));

        builder.ConfigureTestServices(services =>
        {
            // Re-configure the SAME named typed client: only the primary (innermost) handler is
            // replaced, so the resilience pipeline added in Program still wraps it. Last registration wins.
            services.AddHttpClient<ICardServiceClient, CardServiceClient>()
                .ConfigurePrimaryHttpMessageHandler(() => this.handler);
        });
    }
}
