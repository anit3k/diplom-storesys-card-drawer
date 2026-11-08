namespace GameService;

/// <summary>
/// GameService's view of a card, used to deserialize CardService's response.
/// Suit is kept as a string (not CardService's enum) so the two services share no domain types.
/// </summary>
public class Card
{
    public string? Suit { get; set; }
    public string? Rank { get; set; }
    public bool IsJoker { get; set; }
}
