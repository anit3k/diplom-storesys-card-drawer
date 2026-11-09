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

        // 1. Outer: total timeout for the whole operation.
        pipeline.AddTimeout(resilience.TotalTimeout);

        // 2. Retry (circuit breaker is added here on a later step, between retry and the
        //    inner timeout). Sitting between the two timeouts, retry wraps the per-attempt
        //    timeout (so an attempt timeout is retried) but is itself wrapped by the total
        //    timeout (so retries never exceed the overall ceiling).
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

        // 3. Inner: per-attempt timeout.
        pipeline.AddTimeout(resilience.AttemptTimeout);
    });

var app = builder.Build();

app.MapControllers();

app.Run();
