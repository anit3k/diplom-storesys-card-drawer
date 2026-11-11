using CardService;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

// Bootstrap logger: logs anything that fails before the host (and the real logger) is built.
// Replaced by the configured logger once the host starts.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new JsonFormatter(renderMessage: true))
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Structured JSON logging to the console via Serilog (see ADR 0009). One JSON line per
    // event with Timestamp, Level, RenderedMessage, and TraceId/SpanId (Serilog takes the
    // last two from Activity.Current automatically). Minimum levels come from the "Serilog"
    // config section (Debug in Development, Information otherwise).
    builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(new JsonFormatter(renderMessage: true)));

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

    // Liveness health check: always healthy, tagged "liveness", checks no dependencies. See ADR 0008.
    builder.Services.AddHealthChecks()
        .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["liveness"]);

    // DI
    builder.Services.AddSingleton<IDeck, Deck>();
    builder.Services.AddScoped<ICardDrawService, CardDrawService>();
    builder.Services.AddSingleton<IEventStore, EventStore>();
    // Randomness behind an abstraction so tests can inject a deterministic source; production
    // uses Random.Shared via SharedRandomSource.
    builder.Services.AddSingleton<IRandomSource, SharedRandomSource>();

    // Idempotency: key -> card cache so a retried draw returns the same card and raises no new
    // event. IMemoryCache gives us per-entry expiry; the wrapper must be a singleton so the cache
    // and the per-key locks are shared across requests.
    builder.Services.Configure<IdempotencyOptions>(builder.Configuration.GetSection("Idempotency"));
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<IIdempotentCardDraw, IdempotentCardDraw>();

    var app = builder.Build();

    // One summary log line per request (method, path without query, status, elapsed ms), instead
    // of the framework's many per-request lines. /health/live is logged at Debug so it stays quiet.
    app.UseSerilogRequestLogging(options =>
    {
        options.GetLevel = (httpContext, _, exception) =>
            exception is not null ? LogEventLevel.Error
            : httpContext.Response.StatusCode >= 500 ? LogEventLevel.Warning
            : httpContext.Request.Path.StartsWithSegments("/health/live") ? LogEventLevel.Debug
            : LogEventLevel.Information;
    });

    app.MapControllers();

    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("liveness"),
    });

    app.Run();
}
// HostAbortedException is how WebApplicationFactory (tests) stops the host after capturing it;
// it must propagate, not be logged as a fatal startup error.
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "CardService terminated unexpectedly during startup.");
}
finally
{
    Log.CloseAndFlush();
}

// Exposed so Microsoft.AspNetCore.Mvc.Testing's WebApplicationFactory<Program> can boot the host
// in-process for tests.
public partial class Program;
