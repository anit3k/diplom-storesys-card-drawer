using System.Net;
using System.Text.Json;

namespace CardService.UnitTests;

public class IdempotencyTests
{
    private static HttpRequestMessage Draw(string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/cards/random");
        if (idempotencyKey is not null)
        {
            // TryAddWithoutValidation so even a blank value is actually transmitted (HttpClient
            // silently drops an empty value added via the validated Headers.Add).
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private static async Task<int> EventCountAsync(HttpClient client)
    {
        using var json = JsonDocument.Parse(await client.GetStringAsync("/events?start=0&end=100000"));
        return json.RootElement.GetArrayLength();
    }

    [Fact]
    public async Task GetRandom_SameIdempotencyKeyTwice_ReturnsSameCardReplayHeaderAndOneEvent()
    {
        // Distinct index per NEW draw, so a repeat that drew again would differ.
        using var app = new CardServiceApp(new FakeRandomSource(0, 1, 2, 3));
        using var client = app.CreateClient();
        var key = Guid.NewGuid().ToString();

        var first = await client.SendAsync(Draw(key));
        var firstBody = await first.Content.ReadAsStringAsync();

        var second = await client.SendAsync(Draw(key));
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody, secondBody); // same card
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));
        Assert.True(second.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal("true", second.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(1, await EventCountAsync(client)); // only one new event
    }

    [Fact]
    public async Task GetRandom_NewIdempotencyKey_RaisesNewEvent()
    {
        using var app = new CardServiceApp(new FakeRandomSource(0, 1, 2, 3));
        using var client = app.CreateClient();

        await client.SendAsync(Draw(Guid.NewGuid().ToString()));
        await client.SendAsync(Draw(Guid.NewGuid().ToString()));

        Assert.Equal(2, await EventCountAsync(client)); // a different key draws again
    }

    [Fact]
    public async Task GetRandom_NoIdempotencyKey_DrawsEachTime()
    {
        using var app = new CardServiceApp(new FakeRandomSource(0, 1, 2, 3));
        using var client = app.CreateClient();

        var first = await client.SendAsync(Draw(null));
        var second = await client.SendAsync(Draw(null));

        Assert.False(first.Headers.Contains("Idempotency-Replayed"));
        Assert.False(second.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal(2, await EventCountAsync(client)); // unchanged behaviour: two draws, two events
    }

    [Theory]
    [InlineData(" ")]                // blank (whitespace)
    [InlineData(TooLongKey)]         // 129 chars (> 128)
    public async Task GetRandom_InvalidIdempotencyKey_Returns400WithContractErrorShape(string key)
    {
        using var app = new CardServiceApp(new FakeRandomSource(0));
        using var client = app.CreateClient();

        var response = await client.SendAsync(Draw(key));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("invalid_idempotency_key", root.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
        Assert.True(root.TryGetProperty("traceId", out _));
        Assert.Equal(0, await EventCountAsync(client)); // nothing drawn
    }

    private const string TooLongKey =
        "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
        "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"; // 129 chars
}
