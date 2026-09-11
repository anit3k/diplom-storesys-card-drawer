using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CardDrawer;

public class DeckClient : IDeckClient
{
    private const string DeckUrl =
        "https://gist.githubusercontent.com/anit3k/dcbb5ca9d44927ec9a9388327921ee8c/raw/deck.json";

    private readonly HttpClient client;

    public DeckClient(HttpClient client)
    {
        client.BaseAddress = new Uri(DeckUrl);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        this.client = client;
    }

    public async Task<List<Card>> GetFullDeckAsync()
    {
        var response = await this.client.GetAsync(string.Empty);
        response.EnsureSuccessStatusCode();

        var deck = await JsonSerializer.DeserializeAsync<List<Card>>(
            await response.Content.ReadAsStreamAsync(),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            }) ?? new();

        return deck;
    }
}