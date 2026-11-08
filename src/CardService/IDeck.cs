namespace CardService;

public interface IDeck
{
    IReadOnlyList<Card> Cards { get; }
}
