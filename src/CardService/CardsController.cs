using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CardService;

[ApiController]
[Route("cards")]
public class CardsController : ControllerBase
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string IdempotencyReplayedHeader = "Idempotency-Replayed";

    private readonly ICardDrawService cardDrawService;
    private readonly IIdempotentCardDraw idempotentCardDraw;
    private readonly IdempotencyOptions options;

    public CardsController(
        ICardDrawService cardDrawService,
        IIdempotentCardDraw idempotentCardDraw,
        IOptions<IdempotencyOptions> options)
    {
        this.cardDrawService = cardDrawService;
        this.idempotentCardDraw = idempotentCardDraw;
        this.options = options.Value;
    }

    [HttpGet("random")]
    public ActionResult<Card> GetRandomCard()
    {
        // No Idempotency-Key header -> behave exactly as before (backward compatible).
        if (!this.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var headerValues))
        {
            return this.cardDrawService.DrawRandomCard();
        }

        var key = headerValues.ToString();

        // Present but invalid (empty/whitespace or too long) -> 400 with the shared error shape.
        if (string.IsNullOrWhiteSpace(key) || key.Length > this.options.MaxKeyLength)
        {
            return this.BadRequest(new ErrorResponse(
                Error: "invalid_idempotency_key",
                Message: $"The {IdempotencyKeyHeader} header must be non-empty and at most {this.options.MaxKeyLength} characters.",
                TraceId: this.HttpContext.TraceIdentifier));
        }

        var (card, replayed) = this.idempotentCardDraw.DrawOrReplay(key, this.cardDrawService.DrawRandomCard);
        if (replayed)
        {
            this.Response.Headers[IdempotencyReplayedHeader] = "true";
        }

        return card;
    }
}
