using System.Net;

namespace GameService.ServiceTests;

public class CircuitBreakerTests
{
    // Each fully-failed draw makes 3 attempts (2 retries), and the breaker counts every attempt.
    // With MinimumThroughput 6 and FailureRatio 0.5, 2 fully-failed draws = 6 failing samples open
    // the breaker, so the 3rd draw is rejected by the open breaker (verified by the tests below).
    [Fact]
    public async Task Draw_PersistentFailures_OpensBreakerAfterTwoDrawsAndRejectsWithoutCallingMock()
    {
        await using var mock = await MockCardService.StartAsync(MockCardService.AlwaysStatus(503));
        using var app = new GameServiceApp(mock.BaseUrl);
        using var client = app.CreateClient();

        var first = await client.GetAsync("/draw");
        var second = await client.GetAsync("/draw");
        var afterTwo = mock.CallCount;

        var third = await client.GetAsync("/draw");

        Assert.Equal("card_service_unavailable", await first.ErrorCodeAsync());
        Assert.Equal("card_service_unavailable", await second.ErrorCodeAsync());
        Assert.Equal(6, afterTwo); // 3 attempts per failed draw

        Assert.Equal(HttpStatusCode.ServiceUnavailable, third.StatusCode);
        Assert.Equal("card_service_circuit_open", await third.ErrorCodeAsync());
        Assert.Equal(afterTwo, mock.CallCount); // open breaker made no new call to the mock
    }

    [Fact]
    public async Task Draw_AfterBreakDurationWithHealthyService_ClosesAndSucceeds()
    {
        await using var mock = await MockCardService.StartAsync(MockCardService.AlwaysStatus(503));
        using var app = new GameServiceApp(mock.BaseUrl);
        using var client = app.CreateClient();

        await client.GetAsync("/draw");
        await client.GetAsync("/draw"); // breaker now open

        mock.Reprogram(MockCardService.AlwaysCard());

        // While still open, a call is rejected without reaching the mock.
        var whileOpenCount = mock.CallCount;
        var whileOpen = await client.GetAsync("/draw");
        Assert.Equal("card_service_circuit_open", await whileOpen.ErrorCodeAsync());
        Assert.Equal(whileOpenCount, mock.CallCount);

        // After BreakDuration the breaker half-opens; the trial call reaches the now-healthy mock,
        // succeeds, and the breaker closes. Poll (no fixed sleep) until a draw succeeds.
        var countBeforeProbe = mock.CallCount;
        var recovered = await Poll.WaitUntilAsync(
            async () => (await client.GetAsync("/draw")).StatusCode == HttpStatusCode.OK,
            TimeSpan.FromSeconds(5));

        Assert.True(recovered, "breaker did not close within 5s");
        Assert.True(mock.CallCount > countBeforeProbe, "the recovering call should have reached the mock");

        var subsequent = await client.GetAsync("/draw");
        Assert.Equal(HttpStatusCode.OK, subsequent.StatusCode); // stays closed/working
    }

    [Fact]
    public async Task Draw_FailedTrialCallAfterBreak_ReopensBreaker()
    {
        await using var mock = await MockCardService.StartAsync(MockCardService.AlwaysStatus(503));
        using var app = new GameServiceApp(mock.BaseUrl);
        using var client = app.CreateClient();

        await client.GetAsync("/draw");
        await client.GetAsync("/draw"); // breaker now open
        var baseline = mock.CallCount;

        // The mock stays unhealthy. Poll until the half-open trial reaches the mock (one new call).
        var probed = await Poll.WaitUntilAsync(
            async () =>
            {
                await client.GetAsync("/draw");
                return mock.CallCount > baseline;
            },
            TimeSpan.FromSeconds(5));

        Assert.True(probed, "no half-open trial reached the mock within 5s");
        Assert.Equal(baseline + 1, mock.CallCount); // exactly one trial reached the mock

        // The trial failed, so the breaker reopened: the next call is rejected without a new call.
        var afterReopen = mock.CallCount;
        var rejected = await client.GetAsync("/draw");
        Assert.Equal("card_service_circuit_open", await rejected.ErrorCodeAsync());
        Assert.Equal(afterReopen, mock.CallCount);
    }

    [Fact]
    public async Task Draw_Persistent404_NeverOpensBreakerAndCallsMockEveryTime()
    {
        await using var mock = await MockCardService.StartAsync(MockCardService.AlwaysStatus(404));
        using var app = new GameServiceApp(mock.BaseUrl);
        using var client = app.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            var response = await client.GetAsync("/draw");
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode); // 502
            Assert.Equal("card_service_bad_response", await response.ErrorCodeAsync());
        }

        // 404 is not retried and does not count towards the breaker, so every draw reached the mock
        // and the breaker never opened.
        Assert.Equal(10, mock.CallCount);
    }
}
