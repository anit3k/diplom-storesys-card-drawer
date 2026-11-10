namespace GameService;

public interface ICardServiceClient
{
    /// <summary>
    /// Calls CardService for a random card. The idempotency key is sent as the
    /// "Idempotency-Key" header; it must be the SAME for every retry of one draw, so it is
    /// supplied by the caller (per incoming draw) rather than generated here (per attempt).
    /// </summary>
    Task<Card?> GetRandomCardAsync(string idempotencyKey);
}
