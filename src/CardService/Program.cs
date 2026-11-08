using CardService;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// DI
builder.Services.AddSingleton<IDeck, Deck>();
builder.Services.AddScoped<ICardDrawService, CardDrawService>();
builder.Services.AddSingleton<IEventStore, EventStore>();

var app = builder.Build();

app.MapControllers();

app.Run();
