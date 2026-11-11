using System.Text.Json;

namespace CardService.UnitTests;

public class ConcurrencyTests
{
    [Fact]
    public async Task GetRandom_ManyConcurrentCallsSameKey_AllReturnSameCardAndOneEvent()
    {
        // Enough distinct indices that, if the atomic guard failed and several calls drew, the
        // cards would differ and more than one event would be raised.
        using var app = new CardServiceApp(new FakeRandomSource(Enumerable.Range(0, 55).ToArray()));
        using var client = app.CreateClient();
        var key = Guid.NewGuid().ToString();

        var bodies = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/cards/random");
            request.Headers.Add("Idempotency-Key", key);
            var response = await client.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }));

        Assert.Single(bodies.Distinct()); // all 20 got the same card

        using var events = JsonDocument.Parse(await client.GetStringAsync("/events?start=0&end=100000"));
        Assert.Equal(1, events.RootElement.GetArrayLength()); // exactly one event
    }
}
