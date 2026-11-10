using GameService;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Typed HttpClient for CardService; base URL from configuration (CardService:BaseUrl).
builder.Services.Configure<CardServiceOptions>(builder.Configuration.GetSection("CardService"));

// Resilience settings for the CardService call, bound from "CardService:Resilience".
// Validated at startup so the service fails fast on a bad timeout configuration.
builder.Services.AddOptions<CardServiceResilienceOptions>()
    .Bind(builder.Configuration.GetSection("CardService:Resilience"))
    .Validate(o => o.AttemptTimeout > TimeSpan.Zero, "CardService:Resilience:AttemptTimeout must be greater than zero.")
    .Validate(o => o.TotalTimeout > TimeSpan.Zero, "CardService:Resilience:TotalTimeout must be greater than zero.")
    .Validate(o => o.TotalTimeout > o.AttemptTimeout, "CardService:Resilience:TotalTimeout must be greater than AttemptTimeout.")
    .Validate(o => o.MaxRetryAttempts >= 0, "CardService:Resilience:MaxRetryAttempts must be zero or greater.")
    .Validate(o => o.RetryBaseDelay > TimeSpan.Zero, "CardService:Resilience:RetryBaseDelay must be greater than zero.")
    // Worst case without jitter — every attempt hits its timeout plus every back-off wait —
    // must fit inside the total timeout, otherwise the total timeout would cut off the last
    // attempt before the retries are exhausted. Defaults (500 ms, 2 retries, 200 ms, 3 s)
    // give 2.1 s < 3 s and so pass.
    .Validate(
        o => o.WorstCaseWithoutJitter() < o.TotalTimeout,
        "CardService:Resilience: worst case without jitter ((MaxRetryAttempts + 1) * AttemptTimeout + exponential back-off) must be less than TotalTimeout.")
    // Circuit breaker — validate against both Polly's required ranges (verified for the
    // installed version: FailureRatio [0,1], MinimumThroughput [2,maxint], SamplingDuration
    // and BreakDuration [500ms, 1 day]) and our tighter rule FailureRatio in (0, 1].
    .Validate(o => o.FailureRatio > 0 && o.FailureRatio <= 1, "CardService:Resilience:FailureRatio must be in (0, 1].")
    .Validate(o => o.MinimumThroughput >= 2, "CardService:Resilience:MinimumThroughput must be at least 2.")
    .Validate(
        o => o.SamplingDuration >= TimeSpan.FromMilliseconds(500) && o.SamplingDuration <= TimeSpan.FromDays(1),
        "CardService:Resilience:SamplingDuration must be between 500 ms and 1 day.")
    .Validate(
        o => o.BreakDuration >= TimeSpan.FromMilliseconds(500) && o.BreakDuration <= TimeSpan.FromDays(1),
        "CardService:Resilience:BreakDuration must be between 500 ms and 1 day.")
    .ValidateOnStart();

builder.Services.AddHttpClient<ICardServiceClient, CardServiceClient>((serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<CardServiceOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);

        // The resilience pipeline below owns all timeout behaviour, so disable the
        // HttpClient's own default 100s timeout. Otherwise it would sit outside the
        // pipeline and could cut a call short (or mask the pipeline's timeouts) in
        // ways unrelated to the values we configure here.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    // Explicit resilience pipeline (not AddStandardResilienceHandler, whose defaults are
    // tuned for general services and are much longer than we want here). See ADR 0003.
    //
    // Order matters. Strategies added first sit on the OUTSIDE; strategies added last sit
    // on the INSIDE, closest to the HTTP call. So:
    //   1. outer TotalTimeout   -> caps the whole operation (all attempts + back-off waits)
    //   2. retry + circuit breaker (added here on a later step)
    //   3. inner AttemptTimeout -> caps each individual attempt
    // With retries between the two timeouts, the inner one applies per attempt and the
    // outer one guarantees the overall ceiling regardless of how many attempts run.
    .AddResilienceHandler("card-service", (pipeline, context) =>
    {
        var resilience = context.ServiceProvider
            .GetRequiredService<IOptions<CardServiceResilienceOptions>>().Value;
        var cbLogger = context.ServiceProvider
            .GetRequiredService<ILoggerFactory>().CreateLogger("CardServiceCircuitBreaker");

        // Short description of an outcome for the state-change log.
        static string DescribeOutcome(Outcome<HttpResponseMessage> outcome) =>
            outcome.Exception is { } ex
                ? ex.GetType().Name
                : outcome.Result is { } r ? $"HTTP {(int)r.StatusCode}" : "unknown";

        // 1. Outer: total timeout for the whole operation.
        pipeline.AddTimeout(resilience.TotalTimeout);

        // 2. Retry. Sitting between the two timeouts, retry wraps the per-attempt timeout (so
        //    an attempt timeout is retried) but is itself wrapped by the total timeout (so
        //    retries never exceed the overall ceiling). The circuit breaker (step 2b) sits
        //    INSIDE retry, so every attempt is counted by the breaker.
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = resilience.MaxRetryAttempts,
            BackoffType = DelayBackoffType.Exponential,
            Delay = resilience.RetryBaseDelay,
            UseJitter = resilience.UseJitter,

            // Ignore the server's Retry-After header (we use our own back-off schedule).
            ShouldRetryAfterHeader = false,

            // Explicit predicate (not the default IsTransient) so the retried failures are
            // visible here. Retry on: the per-attempt timeout, network failures, and the
            // transient HTTP status codes 408, 429, 502, 503, 504.
            // Do NOT retry on: 500 and other 4xx (deterministic / request faults), caller
            // cancellation, or the total timeout — the last two surface as a cancelled
            // operation, which is excluded below.
            ShouldHandle = args =>
            {
                // Caller cancellation and the outer total timeout surface as cancellation;
                // never retry those.
                if (args.Outcome.Exception is OperationCanceledException)
                {
                    return ValueTask.FromResult(false);
                }

                // The inner per-attempt timeout. (The outer total timeout is outside this
                // retry strategy, so the only TimeoutRejectedException reaching here is a
                // single attempt timing out.)
                if (args.Outcome.Exception is TimeoutRejectedException)
                {
                    return ValueTask.FromResult(true);
                }

                // Network-level failures (connection refused, DNS, socket reset, ...).
                if (args.Outcome.Exception is HttpRequestException)
                {
                    return ValueTask.FromResult(true);
                }

                // Transient HTTP status codes signalled by the server/infrastructure.
                if (args.Outcome.Result is { } response)
                {
                    var retry = response.StatusCode is
                        System.Net.HttpStatusCode.RequestTimeout        // 408
                        or System.Net.HttpStatusCode.TooManyRequests    // 429
                        or System.Net.HttpStatusCode.BadGateway         // 502
                        or System.Net.HttpStatusCode.ServiceUnavailable // 503
                        or System.Net.HttpStatusCode.GatewayTimeout;    // 504
                    return ValueTask.FromResult(retry);
                }

                return ValueTask.FromResult(false);
            },
        });

        // 2b. Circuit breaker. Placed INSIDE retry (so every attempt counts) and OUTSIDE the
        //     per-attempt timeout (so an attempt timeout counts as a failure). The outer total
        //     timeout sits outside the breaker, so it does not count.
        //
        //     NOTE: this failure list is deliberately DIFFERENT from retry's. Retry asks "will
        //     another attempt help?" and so excludes 500. The breaker asks "is the service
        //     healthy?" and so counts ALL 5xx (including 500), plus 408/429, the attempt
        //     timeout and network errors. Other 4xx do NOT count: a client sending bad requests
        //     should not be able to open the breaker for everyone.
        pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = resilience.FailureRatio,
            MinimumThroughput = resilience.MinimumThroughput,
            SamplingDuration = resilience.SamplingDuration,
            BreakDuration = resilience.BreakDuration,

            ShouldHandle = args =>
            {
                // Do not count caller cancellation or the outer total timeout (both surface as
                // a cancelled operation).
                if (args.Outcome.Exception is OperationCanceledException)
                {
                    return ValueTask.FromResult(false);
                }

                // Per-attempt timeout and network failures count as failures.
                if (args.Outcome.Exception is TimeoutRejectedException or HttpRequestException)
                {
                    return ValueTask.FromResult(true);
                }

                // Count ALL 5xx (including 500), plus 408 and 429. Other 4xx do not count.
                if (args.Outcome.Result is { } response)
                {
                    var status = (int)response.StatusCode;
                    var countsAsFailure =
                        status >= 500 && status <= 599
                        || response.StatusCode is System.Net.HttpStatusCode.RequestTimeout    // 408
                        or System.Net.HttpStatusCode.TooManyRequests;                          // 429
                    return ValueTask.FromResult(countsAsFailure);
                }

                return ValueTask.FromResult(false);
            },

            // Log each state change (simple text; structured logging comes later).
            OnOpened = args =>
            {
                cbLogger.LogWarning(
                    "Circuit breaker OPENED for {BreakDuration} (last outcome: {Outcome}).",
                    args.BreakDuration,
                    DescribeOutcome(args.Outcome));
                return default;
            },
            OnClosed = _ =>
            {
                cbLogger.LogInformation("Circuit breaker CLOSED (service healthy again).");
                return default;
            },
            OnHalfOpened = _ =>
            {
                cbLogger.LogInformation("Circuit breaker HALF-OPEN (letting one trial call through).");
                return default;
            },
        });

        // 3. Inner: per-attempt timeout.
        pipeline.AddTimeout(resilience.AttemptTimeout);
    });

var app = builder.Build();

app.MapControllers();

app.Run();
