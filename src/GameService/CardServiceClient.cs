using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace GameService;

/// <summary>
/// Typed HttpClient for CardService. The base URL comes from configuration (CardService:BaseUrl),
/// and the resilience pipeline (timeout + retry + circuit breaker) is wired up in Program.cs.
///
/// This client is the ONLY place that knows about the resilience library: it translates the
/// library's exceptions and the HTTP outcome into a <see cref="CardServiceException"/> with a
/// <see cref="CardServiceFailure"/> cause, so the controller and the central error handling stay
/// free of Polly / HttpClient concerns.
/// </summary>
public class CardServiceClient : ICardServiceClient
{
    private readonly HttpClient httpClient;

    public CardServiceClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<Card> GetRandomCardAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            // Build the request ourselves so we can attach the idempotency key header. The same
            // HttpRequestMessage instance is re-sent by the resilience pipeline on each retry, so
            // every attempt of this draw carries the SAME key.
            using var request = new HttpRequestMessage(HttpMethod.Get, "cards/random");
            request.Headers.Add("Idempotency-Key", idempotencyKey);

            response = await this.httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away — not an error of ours. Let it propagate; no response is sent.
            throw;
        }
        catch (TimeoutRejectedException ex)
        {
            // A per-attempt timeout (retries exhausted) or the overall timeout.
            throw new CardServiceException(CardServiceFailure.Timeout, "CardService call timed out.", ex);
        }
        catch (BrokenCircuitException ex)
        {
            // The circuit breaker is open: the call was rejected without being sent.
            throw new CardServiceException(CardServiceFailure.CircuitOpen, "CardService circuit is open.", ex);
        }
        catch (HttpRequestException ex)
        {
            // Connection refused, DNS failure, socket reset, ... — CardService is unreachable.
            throw new CardServiceException(CardServiceFailure.Unavailable, "CardService is unreachable.", ex);
        }

        using (response)
        {
            // HttpClient does not throw on 5xx/4xx (we removed EnsureSuccessStatusCode), so we
            // inspect the status ourselves. 503 means "temporarily unavailable"; any other
            // non-success is an unexpected/bad response.
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                throw new CardServiceException(
                    CardServiceFailure.Unavailable, "CardService returned 503 after retries.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new CardServiceException(
                    CardServiceFailure.BadResponse,
                    $"CardService returned unexpected status {(int)response.StatusCode}.");
            }

            try
            {
                var card = await response.Content.ReadFromJsonAsync<Card>(cancellationToken);
                if (card is null)
                {
                    throw new CardServiceException(
                        CardServiceFailure.BadResponse, "CardService returned an empty body.");
                }

                return card;
            }
            catch (JsonException ex)
            {
                throw new CardServiceException(
                    CardServiceFailure.BadResponse, "CardService returned an invalid JSON body.", ex);
            }
        }
    }
}
