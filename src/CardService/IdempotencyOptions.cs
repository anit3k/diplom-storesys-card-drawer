namespace CardService;

/// <summary>
/// Settings for idempotent card draws, bound from the "Idempotency" section of
/// appsettings.json. The cache maps an idempotency key to the card that was drawn for it,
/// so a repeated key (e.g. a client retry after a timeout) returns the same card and raises
/// no new event. Entries expire so the cache cannot grow without bound.
/// </summary>
public class IdempotencyOptions
{
    /// <summary>
    /// How long a key -> card mapping is kept. Default 5 minutes — long enough to cover a
    /// client's retries of one draw, short enough to bound memory.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum accepted key length. Keys longer than this are rejected with 400.
    /// </summary>
    public int MaxKeyLength { get; set; } = 128;
}
