using CardService;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Liveness health check: "is the process up and able to answer?" Always healthy, tagged
// "liveness", and deliberately checks NO dependencies (see ADR 0008). Mapped below.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["liveness"]);

// DI
builder.Services.AddSingleton<IDeck, Deck>();
builder.Services.AddScoped<ICardDrawService, CardDrawService>();
builder.Services.AddSingleton<IEventStore, EventStore>();

// Idempotency: key -> card cache so a retried draw returns the same card and raises no new
// event. IMemoryCache gives us per-entry expiry; the wrapper must be a singleton so the cache
// and the per-key locks are shared across requests.
builder.Services.Configure<IdempotencyOptions>(builder.Configuration.GetSection("Idempotency"));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IIdempotentCardDraw, IdempotentCardDraw>();

var app = builder.Build();

app.MapControllers();

// GET /health/live -> 200 when the process is up. Liveness only (no /health/startup or
// /health/ready), and no dependency checks. See ADR 0008.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("liveness"),
});

app.Run();
