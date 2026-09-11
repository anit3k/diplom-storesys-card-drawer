using Microsoft.AspNetCore.Mvc;

namespace CardDrawer;

[ApiController]
[Route("cards")]
public class CardsController : ControllerBase
{
    private readonly ICardDrawService cardDrawService;
    
    public CardsController(ICardDrawService cardDrawService)
    {
        this.cardDrawService = cardDrawService;
    }
    
    [HttpGet("random")]
    public async Task<ActionResult<Card>> GetRandomCard()
    {
        var card = await this.cardDrawService.DrawRandomCard();
        return card;
    }
}