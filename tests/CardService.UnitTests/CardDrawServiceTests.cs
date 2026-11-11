using CardService;

namespace CardService.UnitTests;

public class CardDrawServiceTests
{
    [Fact]
    public void DrawRandomCard_PicksIndexInDeckRange_AndReturnsCardAtThatIndex()
    {
        var deck = new Deck();
        var random = new FakeRandomSource(7); // force index 7
        var sut = new CardDrawService(deck, new FakeEventStore(), random);

        var card = sut.DrawRandomCard();

        // The draw asked for an index in [0, 55) (the deck size)...
        Assert.Equal([55], random.RequestedMaxExclusive);
        // ...and returned exactly the card at that index — no weighting.
        Assert.Same(deck.Cards[7], card);
    }

    [Fact]
    public void DrawRandomCard_OverAllIndices_ReturnsEveryCardExactlyOnce()
    {
        var deck = new Deck();
        var allIndices = Enumerable.Range(0, deck.Cards.Count).ToArray(); // 0..54
        var random = new FakeRandomSource(allIndices);
        var sut = new CardDrawService(deck, new FakeEventStore(), random);

        var drawn = Enumerable.Range(0, deck.Cards.Count)
            .Select(_ => sut.DrawRandomCard())
            .ToList();

        // Sweeping every index yields each of the 55 distinct deck entries exactly once.
        Assert.Equal(deck.Cards.Count, drawn.Count);
        Assert.Equal(deck.Cards, drawn); // same entries, same order (reference equality)
        Assert.All(random.RequestedMaxExclusive, max => Assert.Equal(55, max));
    }

    [Fact]
    public void DrawRandomCard_RaisesOneCardDrawnEvent_WithTheDrawnCard()
    {
        var deck = new Deck();
        var events = new FakeEventStore();
        var sut = new CardDrawService(deck, events, new FakeRandomSource(0));

        var card = sut.DrawRandomCard();

        Assert.Single(events.Raised);
        Assert.Same(card, events.Raised[0]);
    }
}
