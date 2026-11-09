using GameService;
using Microsoft.Extensions.Options;
using Polly;

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

        // 2. Placeholder: retry and circuit breaker are added here on later steps.
        //    They go BETWEEN the two timeouts on purpose (see ordering note above),
        //    so no restructuring is needed to slot them in.

        // 3. Inner: per-attempt timeout.
        pipeline.AddTimeout(resilience.AttemptTimeout);
    });

var app = builder.Build();

app.MapControllers();

app.Run();
