using System.ComponentModel.DataAnnotations;

namespace GameService;

/// <summary>
/// Resilience settings for the HTTP calls GameService makes to CardService.
/// Bound from the "CardService:Resilience" section of appsettings.json and validated
/// at startup (see Program.cs). Values are configurable so tests can override them
/// with much shorter durations (e.g. an attempt timeout of a few milliseconds).
/// Retry and circuit breaker are added in later steps; this evening only the two
/// timeouts below are configured.
/// </summary>
public class CardServiceResilienceOptions
{
    /// <summary>
    /// Timeout for a single attempt (one HTTP request to CardService). Keeps one hanging
    /// attempt from consuming the whole budget, leaving room for retries later.
    /// </summary>
    public TimeSpan AttemptTimeout { get; set; }

    /// <summary>
    /// Timeout for the whole operation (all attempts plus any back-off waits between them).
    /// Guarantees an upper bound on how long the caller waits, regardless of retries.
    /// </summary>
    public TimeSpan TotalTimeout { get; set; }
}
