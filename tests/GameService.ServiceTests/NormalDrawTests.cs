using System.Net;
using System.Text.Json;

namespace GameService.ServiceTests;

public class NormalDrawTests
{
    [Fact]
    public async Task Draw_MockReturnsCard_Returns200AndCardAndCallsMockOnce()
    {
        await using var mock = await MockCardService.StartAsync(MockCardService.AlwaysCard());
        using var app = new GameServiceApp(mock.BaseUrl);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/draw");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Hearts", json.RootElement.GetProperty("suit").GetString());
        Assert.False(json.RootElement.GetProperty("isJoker").GetBoolean());
        Assert.Equal(1, mock.CallCount);
    }

    [Fact]
    public async Task Draw_WithKnownTraceparent_PropagatesSameTraceIdToCardService()
    {
        const string traceId = "0af7651916cd43dd8448eb211c80319c";

        await using var mock = await MockCardService.StartAsync(MockCardService.AlwaysCard());
        using var app = new GameServiceApp(mock.BaseUrl);
        using var client = app.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/draw");
        request.Headers.Add("traceparent", $"00-{traceId}-b7ad6b7169203331-01");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var recorded = Assert.Single(mock.Calls);
        Assert.NotNull(recorded.Traceparent);
        // HttpClient forwards W3C trace context: the mock's traceparent carries the same trace id
        // (a new span id), proving propagation across the two services.
        Assert.Contains(traceId, recorded.Traceparent);
    }
}
