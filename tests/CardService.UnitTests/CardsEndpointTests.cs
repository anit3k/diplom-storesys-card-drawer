using System.Net;
using System.Text.Json;

namespace CardService.UnitTests;

public class CardsEndpointTests
{
    [Fact]
    public async Task GetRandom_OrdinaryCard_Returns200AndExpectedJsonShape()
    {
        // Index 0 is the first ordinary card (Clubs "2").
        using var app = new CardServiceApp(new FakeRandomSource(0));
        using var client = app.CreateClient();

        var response = await client.GetAsync("/cards/random");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(JsonValueKind.String, root.GetProperty("suit").ValueKind);
        Assert.Equal("Clubs", root.GetProperty("suit").GetString());
        Assert.Equal("2", root.GetProperty("rank").GetString());
        Assert.False(root.GetProperty("isJoker").GetBoolean());
    }

    [Fact]
    public async Task GetRandom_Joker_Returns200AndNullSuitAndRank()
    {
        // Index 52 is the first joker.
        using var app = new CardServiceApp(new FakeRandomSource(52));
        using var client = app.CreateClient();

        var response = await client.GetAsync("/cards/random");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        // A joker has no suit and no rank, and isJoker is true (per docs/api-contract.md).
        Assert.Equal(JsonValueKind.Null, root.GetProperty("suit").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("rank").ValueKind);
        Assert.True(root.GetProperty("isJoker").GetBoolean());
    }
}
