namespace GameService;

/// <summary>
/// Resilience settings for the HTTP calls GameService makes to CardService.
/// Bound from the "CardService:Resilience" section of appsettings.json and validated
/// at startup (see Program.cs). Values are configurable so tests can override them
/// (e.g. a short retry base delay and jitter off for deterministic results).
/// Circuit breaker is added in a later step.
/// </summary>
public class CardServiceResilienceOptions
{
    /// <summary>
    /// Timeout for a single attempt (one HTTP request to CardService). Keeps one hanging
    /// attempt from consuming the whole budget, leaving room for retries.
    /// </summary>
    public TimeSpan AttemptTimeout { get; set; }

    /// <summary>
    /// Timeout for the whole operation (all attempts plus any back-off waits between them).
    /// Guarantees an upper bound on how long the caller waits, regardless of retries.
    /// </summary>
    public TimeSpan TotalTimeout { get; set; }

    /// <summary>
    /// Number of retries after the first attempt. 2 means up to 3 attempts in total.
    /// </summary>
    public int MaxRetryAttempts { get; set; }

    /// <summary>
    /// Base delay for the exponential back-off between retries (e.g. 200 ms gives waits of
    /// roughly 200 ms then 400 ms). Tests can set this very low for speed.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; }

    /// <summary>
    /// Whether to add jitter to the back-off. On in production (so many clients do not retry
    /// in lockstep); tests turn it off for deterministic timing.
    /// </summary>
    public bool UseJitter { get; set; }

    /// <summary>
    /// Worst-case duration WITHOUT jitter: every attempt hits its timeout, plus every
    /// exponential back-off wait in full. This is
    /// (MaxRetryAttempts + 1) x AttemptTimeout + sum of the back-off waits, where the waits
    /// are RetryBaseDelay x (2^0 + 2^1 + ... + 2^(MaxRetryAttempts-1)) =
    /// RetryBaseDelay x (2^MaxRetryAttempts - 1). This must stay below TotalTimeout, or the
    /// total timeout would cut off the last attempt before the retries are exhausted.
    /// (Jitter only lowers the back-off waits here — see ADR 0004 — so the no-jitter figure
    /// is a safe upper bound.)
    /// </summary>
    public TimeSpan WorstCaseWithoutJitter()
    {
        var attempts = (MaxRetryAttempts + 1) * AttemptTimeout;
        var backoffMultiplier = Math.Pow(2, MaxRetryAttempts) - 1; // 2^0 + ... + 2^(n-1)
        var backoff = RetryBaseDelay * backoffMultiplier;
        return attempts + backoff;
    }
}
