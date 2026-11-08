namespace GameService;

public interface ICardServiceClient
{
    Task<Card?> GetRandomCardAsync();
}
