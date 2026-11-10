using Microsoft.AspNetCore.Mvc;

namespace GameService;

[ApiController]
[Route("draw")]
public class DrawController : ControllerBase
{
    private readonly ICardServiceClient cardServiceClient;

    public DrawController(ICardServiceClient cardServiceClient)
    {
        this.cardServiceClient = cardServiceClient;
    }

    [HttpGet]
    public async Task<ActionResult<Card>> Draw()
    {
        // One idempotency key per incoming draw. It is generated here (not per attempt) so
        // that all retries of THIS draw reuse the same key, letting CardService recognise a
        // retried-after-timeout call and return the same card without drawing again.
        var idempotencyKey = Guid.NewGuid().ToString();

        var card = await this.cardServiceClient.GetRandomCardAsync(idempotencyKey);
        return card is null ? this.NotFound() : card;
    }
}
