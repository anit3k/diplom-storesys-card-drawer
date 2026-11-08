using GameService;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Typed HttpClient for CardService; base URL from configuration (CardService:BaseUrl).
builder.Services.Configure<CardServiceOptions>(builder.Configuration.GetSection("CardService"));
builder.Services.AddHttpClient<ICardServiceClient, CardServiceClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<CardServiceOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
});

var app = builder.Build();

app.MapControllers();

app.Run();
