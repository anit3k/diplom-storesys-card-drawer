using System.Diagnostics;
using System.Net;

namespace GameService.ServiceTests;

public class TimeoutAndRetryTests
{
    [Fact]
    public async Task Draw_MockSlow_Returns504AfterRetriesAndWellBeforeThreeDelays()
    {
        var delay = TimeSpan.FromMilliseconds(500); // ~5x the 100ms attempt timeout
        await using var mock = await MockCardService.StartAsync(MockCardService.Delay(delay));
        using var app = new GameServiceApp(mock.BaseUrl);
        using var client = app.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync("/draw");
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode); // 504
        Assert.Equal("card_service_timeout", await response.ErrorCodeAsync());
        Assert.Equal(3, mock.CallCount); // each attempt timed out and was retried
        // Replies well before three full delays (3 x 500ms = 1500ms) would have elapsed.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(1000),
            $"Draw took {stopwatch.ElapsedMilliseconds} ms; expected well under 1500 ms.");
    }

    [Fact]
    public async Task Draw_TransientFailuresThenSuccess_Returns200With3CallsSharingOneIdempotencyKey()
    {
        await using var mock = await MockCardService.StartAsync(MockCardService.FailThenCard(2, status: 503));
        using var app = new GameServiceApp(mock.BaseUrl);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/draw");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, mock.CallCount); // 503, 503, 200
        var keys = mock.Calls.Select(c => c.IdempotencyKey).ToList();
        Assert.All(keys, k => Assert.False(string.IsNullOrEmpty(k)));
        Assert.Single(keys.Distinct()); // all three attempts of this one draw share the key
    }

    [Fact]
    public async Task Draw_FirstAttemptTooSlowThenSuccess_Returns200With2Calls()
    {
        await using var mock = await MockCardService.StartAsync(
            MockCardService.SlowThenCard(TimeSpan.FromMilliseconds(500)));
        using var app = new GameServiceApp(mock.BaseUrl);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/draw");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, mock.CallCount); // first attempt timed out, retry succeeded
    }
}
