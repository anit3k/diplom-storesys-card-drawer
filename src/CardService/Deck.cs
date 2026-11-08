namespace CardService;

/// <summary>
/// The full card deck, generated locally in code: 4 suits x 13 ranks + 3 jokers = 55 cards.
/// Every card is a distinct entry, so drawing one uniformly at random gives each card equal probability.
/// </summary>
public class Deck : IDeck
{
    private static readonly string[] Ranks =
    {
        "2", "3", "4", "5", "6", "7", "8", "9", "10", "Jack", "Queen", "King", "Ace"
    };

    private const int JokerCount = 3;

    public Deck()
    {
        var cards = new List<Card>(Enum.GetValues<Suit>().Length * Ranks.Length + JokerCount);

        foreach (var suit in Enum.GetValues<Suit>())
        {
            foreach (var rank in Ranks)
            {
                cards.Add(new Card { Suit = suit, Rank = rank, IsJoker = false });
            }
        }

        for (var i = 0; i < JokerCount; i++)
        {
            cards.Add(new Card { Suit = null, Rank = null, IsJoker = true });
        }

        this.Cards = cards;
    }

    public IReadOnlyList<Card> Cards { get; }
}
