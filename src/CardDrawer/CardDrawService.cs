namespace CardDrawer;

public class CardDrawService : ICardDrawService
{
    private readonly IDeckClient deckClient;

    public CardDrawService(IDeckClient deckClient)
    {
        this.deckClient = deckClient;
    }

    public async Task<Card> DrawRandomCard()
    {
        var deck = await this.deckClient.GetFullDeckAsync();
        var index = Random.Shared.Next(deck.Count);
        return deck[index];
    }
}