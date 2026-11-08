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
        var card = await this.cardServiceClient.GetRandomCardAsync();
        return card is null ? this.NotFound() : card;
    }
}
