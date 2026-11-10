namespace GameService;

/// <summary>
/// Why a call to CardService failed, in GameService's own terms. The typed client translates
/// the resilience library's exceptions and HTTP outcomes into these causes, so the controller
/// and the central error handling never need to know about Polly / the HTTP client.
/// </summary>
public enum CardServiceFailure
{
    /// <summary>A per-attempt or the overall timeout elapsed.</summary>
    Timeout,

    /// <summary>CardService could not be reached or reported itself unavailable (connection
    /// refused, network error, or HTTP 503 after all retries).</summary>
    Unavailable,

    /// <summary>The circuit breaker is open, so the call was rejected without being sent.</summary>
    CircuitOpen,

    /// <summary>CardService answered, but with something we cannot use (HTTP 500, another
    /// non-503 5xx, a 4xx, or an invalid/incomplete body).</summary>
    BadResponse,
}

/// <summary>
/// Raised by <see cref="CardServiceClient"/> when a card could not be drawn. Carries a
/// <see cref="CardServiceFailure"/> cause; the message/inner exception are for server-side
/// logging only and are never sent to the caller.
/// </summary>
public class CardServiceException : Exception
{
    public CardServiceException(CardServiceFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        this.Failure = failure;
    }

    public CardServiceFailure Failure { get; }
}
