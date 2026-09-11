namespace CardDrawer;

public enum Suit
{
    Clubs,
    Diamonds,
    Hearts,
    Spades
}

public class Card
{
    public Suit? Suit { get; set; }
    public string? Rank { get; set; }
    public bool IsJoker { get; set; }
}