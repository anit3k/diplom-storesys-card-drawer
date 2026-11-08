namespace GameService;

/// <summary>
/// Configuration for reaching CardService, bound from the "CardService" section of appsettings.json.
/// </summary>
public class CardServiceOptions
{
    public string BaseUrl { get; set; } = string.Empty;
}
