using CardService;

public class CardDrawService : ICardDrawService
{
    private readonly IDeck deck;
    private readonly IEventStore eventStore;
    private readonly IRandomSource random;

    public CardDrawService(IDeck deck, IEventStore eventStore, IRandomSource random)
    {
        this.deck = deck;
        this.eventStore = eventStore;
        this.random = random;
    }

    public Card DrawRandomCard()
    {
        var card = this.deck.Cards[this.random.Next(this.deck.Cards.Count)];
        this.eventStore.Raise("CardDrawn", card);
        return card;
    }
}
