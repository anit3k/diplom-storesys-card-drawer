using CardService;

namespace CardService.UnitTests;

public class DeckTests
{
    private static readonly string[] ExpectedRanks =
        ["2", "3", "4", "5", "6", "7", "8", "9", "10", "Jack", "Queen", "King", "Ace"];

    [Fact]
    public void Deck_WhenBuilt_Has55Cards()
    {
        var deck = new Deck();

        Assert.Equal(55, deck.Cards.Count);
    }

    [Fact]
    public void Deck_WhenBuilt_Has52DistinctOrdinaryCardsCoveringEverySuitAndRank()
    {
        var deck = new Deck();

        var ordinary = deck.Cards.Where(c => !c.IsJoker).ToList();
        Assert.Equal(52, ordinary.Count);

        // Every ordinary card has a suit and rank, and the (suit, rank) pairs are exactly the
        // full 4 x 13 product with no duplicates.
        Assert.All(ordinary, c =>
        {
            Assert.NotNull(c.Suit);
            Assert.NotNull(c.Rank);
        });

        var actual = ordinary.Select(c => (c.Suit!.Value, c.Rank!)).ToHashSet();
        var expected = (from suit in Enum.GetValues<Suit>()
                        from rank in ExpectedRanks
                        select (suit, rank)).ToHashSet();

        Assert.Equal(52, actual.Count); // all distinct
        Assert.Equal(expected, actual); // and exactly the expected combinations
    }

    [Fact]
    public void Deck_WhenBuilt_HasExactly3Jokers()
    {
        var deck = new Deck();

        var jokers = deck.Cards.Where(c => c.IsJoker).ToList();
        Assert.Equal(3, jokers.Count);

        // A joker has no suit and no rank (per the API contract).
        Assert.All(jokers, j =>
        {
            Assert.Null(j.Suit);
            Assert.Null(j.Rank);
        });
    }
}
