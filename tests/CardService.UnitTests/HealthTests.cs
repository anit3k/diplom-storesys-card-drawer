using System.Net;

namespace CardService.UnitTests;

public class HealthTests
{
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        using var app = new CardServiceApp();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
