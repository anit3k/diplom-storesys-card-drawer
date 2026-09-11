namespace CardDrawer;

public interface ICardDrawService
{
    Task<Card> DrawRandomCard();
}