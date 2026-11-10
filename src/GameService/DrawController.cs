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
    public async Task<ActionResult<Card>> Draw(CancellationToken cancellationToken)
    {
        // One idempotency key per incoming draw. It is generated here (not per attempt) so
        // that all retries of THIS draw reuse the same key, letting CardService recognise a
        // retried-after-timeout call and return the same card without drawing again.
        var idempotencyKey = Guid.NewGuid().ToString();

        // No try/catch: the client throws CardServiceException on failure, and the central
        // exception handler (see CardServiceExceptionHandler) maps it to the right status/body.
        var card = await this.cardServiceClient.GetRandomCardAsync(idempotencyKey, cancellationToken);
        return card;
    }
}
