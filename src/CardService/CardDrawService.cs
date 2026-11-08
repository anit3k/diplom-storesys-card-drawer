using CardService;

public class CardDrawService : ICardDrawService
{
    private readonly IDeck deck;
    private readonly IEventStore eventStore;

    public CardDrawService(IDeck deck, IEventStore eventStore)
    {
        this.deck = deck;
        this.eventStore = eventStore;
    }

    public Card DrawRandomCard()
    {
        var card = this.deck.Cards[Random.Shared.Next(this.deck.Cards.Count)];
        this.eventStore.Raise("CardDrawn", card);
        return card;
    }
}
