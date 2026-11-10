using CardService;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

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

app.Run();
