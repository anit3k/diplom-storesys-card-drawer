using System.Net.Http.Json;

namespace GameService;

/// <summary>
/// Typed HttpClient for CardService. The base URL comes from configuration (CardService:BaseUrl),
/// wired up in Program.cs. No resilience (timeout/retry/circuit breaker) yet — happy path only.
/// </summary>
public class CardServiceClient : ICardServiceClient
{
    private readonly HttpClient httpClient;

    public CardServiceClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<Card?> GetRandomCardAsync()
    {
        return await this.httpClient.GetFromJsonAsync<Card>("cards/random");
    }
}
