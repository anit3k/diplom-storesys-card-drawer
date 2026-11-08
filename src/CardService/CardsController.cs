using Microsoft.AspNetCore.Mvc;

namespace CardService;

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
    public ActionResult<Card> GetRandomCard()
    {
        var card = this.cardDrawService.DrawRandomCard();
        return card;
    }
}