using System.Text.Json;

namespace CardService.UnitTests;

public class EventFeedTraceTests
{
    [Fact]
    public async Task GetRandom_WithTraceparent_EventCarriesThatTraceId()
    {
        const string traceId = "0af7651916cd43dd8448eb211c80319c";
        var traceparent = $"00-{traceId}-b7ad6b7169203331-01";

        using var app = new CardServiceApp(new FakeRandomSource(0));
        using var client = app.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/cards/random");
        request.Headers.Add("traceparent", traceparent);
        await client.SendAsync(request);

        using var events = JsonDocument.Parse(await client.GetStringAsync("/events?start=0&end=100000"));
        var ev = Assert.Single(events.RootElement.EnumerateArray().ToList());
        Assert.Equal(traceId, ev.GetProperty("traceId").GetString());
    }
}
