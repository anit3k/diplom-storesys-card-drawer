using System.Diagnostics;
using System.Net;

namespace GameService.ServiceTests;

public class UnavailableCardServiceTests
{
    [Fact]
    public async Task Draw_CardServiceNotListening_ReturnsServerErrorQuicklyWithoutHanging()
    {
        // Start the mock to obtain a real loopback address, then stop it so the port has no listener.
        var mock = await MockCardService.StartAsync(MockCardService.AlwaysCard());
        var baseUrl = mock.BaseUrl;
        await mock.DisposeAsync();

        using var app = new GameServiceApp(baseUrl);
        using var client = app.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync("/draw");
        stopwatch.Stop();

        // GameService must fail with a server error and NOT hang. Which 5xx it is depends on how
        // quickly the OS reports the refused loopback connection: if the refusal surfaces before the
        // 100ms attempt timeout it is 503 card_service_unavailable; if the attempt timeout fires
        // first (loopback refusal can be slow on Windows) it is 504 card_service_timeout. Either is
        // a correct, bounded failure.
        Assert.True(
            response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
            $"expected 503 or 504 but got {(int)response.StatusCode}");
        var error = await response.ErrorCodeAsync();
        Assert.True(
            error is "card_service_unavailable" or "card_service_timeout",
            $"unexpected error code '{error}'");

        // Does not hang: well within the total timeout budget plus margin.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"draw took {stopwatch.ElapsedMilliseconds} ms; expected a prompt failure.");
    }
}
