namespace CardDrawer;

public interface IDeckClient
{
    Task<List<Card>> GetFullDeckAsync();
}