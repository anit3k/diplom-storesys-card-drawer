using System.Net.Http.Json;

namespace GameService;

/// <summary>
/// Typed HttpClient for CardService. The base URL comes from configuration (CardService:BaseUrl),
/// and the resilience pipeline (timeout + retry) is wired up in Program.cs.
/// </summary>
public class CardServiceClient : ICardServiceClient
{
    private readonly HttpClient httpClient;

    public CardServiceClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<Card?> GetRandomCardAsync(string idempotencyKey)
    {
        // Build the request ourselves so we can attach the idempotency key header. The same
        // HttpRequestMessage instance is re-sent by the resilience pipeline on each retry, so
        // every attempt of this draw carries the SAME key (verified against the fake service).
        using var request = new HttpRequestMessage(HttpMethod.Get, "cards/random");
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await this.httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Card>();
    }
}
