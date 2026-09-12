using CardDrawer;

public class CardDrawService : ICardDrawService
{
    private readonly IDeckClient deckClient;
    private readonly IEventStore eventStore;

    public CardDrawService(IDeckClient deckClient, IEventStore eventStore)
    {
        this.deckClient = deckClient;
        this.eventStore = eventStore;
    }

    public async Task<Card> DrawRandomCard()
    {
        var deck = await this.deckClient.GetFullDeckAsync();
        var card = deck[Random.Shared.Next(deck.Count)];
        this.eventStore.Raise("CardDrawn", card);
        return card;
    }
}