using CardDrawer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// DI
builder.Services.AddHttpClient<IDeckClient, DeckClient>();
builder.Services.AddScoped<ICardDrawService, CardDrawService>();
builder.Services.AddSingleton<IEventStore, EventStore>();

var app = builder.Build();

app.UseHttpsRedirection();
app.MapControllers();

app.Run();
