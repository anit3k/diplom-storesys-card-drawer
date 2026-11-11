using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace GameService.UnitTests;

public class DrawEndpointTests
{
    [Fact]
    public async Task Draw_OrdinaryCard_Returns200AndCardAndCallsCardServiceOnce()
    {
        var handler = new ScriptedHttpMessageHandler(ScriptedHttpMessageHandler.OrdinaryCard("Spades", "Ace"));
        using var app = new GameServiceApp(handler);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/draw");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Spades", json.RootElement.GetProperty("suit").GetString());
        Assert.Equal("Ace", json.RootElement.GetProperty("rank").GetString());
        Assert.False(json.RootElement.GetProperty("isJoker").GetBoolean());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Draw_Joker_Returns200AndJoker()
    {
        var handler = new ScriptedHttpMessageHandler(ScriptedHttpMessageHandler.Joker());
        using var app = new GameServiceApp(handler);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/draw");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("suit").ValueKind);
        Assert.True(json.RootElement.GetProperty("isJoker").GetBoolean());
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]         // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)] // 503
    [InlineData(HttpStatusCode.GatewayTimeout)]     // 504
    [InlineData(HttpStatusCode.RequestTimeout)]     // 408
    [InlineData((HttpStatusCode)429)]               // 429
    public async Task Draw_TransientThenSuccess_RetriesOnceAndReturns200(HttpStatusCode transient)
    {
        var handler = new ScriptedHttpMessageHandler(
            ScriptedHttpMessageHandler.Respond(transient),
            ScriptedHttpMessageHandler.OrdinaryCard());
        using var app = new GameServiceApp(handler);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/draw");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.CallCount); // first failed, retry succeeded
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)] // 500
    [InlineData(HttpStatusCode.BadRequest)]          // 400
    [InlineData(HttpStatusCode.NotFound)]            // 404
    public async Task Draw_NonRetriableStatus_NoRetryAndReturns502(HttpStatusCode status)
    {
        var handler = new ScriptedHttpMessageHandler(ScriptedHttpMessageHandler.Respond(status));
        using var app = new GameServiceApp(handler);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/draw");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode); // 502
        Assert.Equal("card_service_bad_response", await ErrorCodeAsync(response));
        Assert.Equal(1, handler.CallCount); // no retry
    }

    [Fact]
    public async Task Draw_Persistent503_RetriesThenReturns503Unavailable()
    {
        var handler = new ScriptedHttpMessageHandler(
            ScriptedHttpMessageHandler.Respond(HttpStatusCode.ServiceUnavailable));
        using var app = new GameServiceApp(handler);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/draw");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("card_service_unavailable", await ErrorCodeAsync(response));
        Assert.Equal(3, handler.CallCount); // 1 + 2 retries
    }

    [Fact]
    public async Task Draw_HttpRequestException_RetriesThenReturns503Unavailable()
    {
        var handler = new ScriptedHttpMessageHandler(
            ScriptedHttpMessageHandler.Throw(new HttpRequestException("connection refused")));
        using var app = new GameServiceApp(handler);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/draw");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("card_service_unavailable", await ErrorCodeAsync(response));
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Draw_SlowResponse_TimesOutWith504AndReturnsWellBeforeThreeDelays()
    {
        var delay = TimeSpan.FromMilliseconds(500); // ~5x the 100ms attempt timeout
        var handler = new ScriptedHttpMessageHandler(ScriptedHttpMessageHandler.DelayThenOk(delay));
        using var app = new GameServiceApp(handler);
        using var client = app.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync("/draw");
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode); // 504
        Assert.Equal("card_service_timeout", await ErrorCodeAsync(response));
        Assert.Equal(3, handler.CallCount); // each attempt timed out and was retried
        // The reply arrives well before three full delays would have elapsed (3 x 500ms = 1500ms).
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(1000),
            $"Draw took {stopwatch.ElapsedMilliseconds} ms; expected well under 1500 ms.");
    }

    [Fact]
    public async Task Draw_RetriedAttempts_AllCarryTheSameIdempotencyKey()
    {
        var handler = new ScriptedHttpMessageHandler(
            ScriptedHttpMessageHandler.Respond(HttpStatusCode.ServiceUnavailable),
            ScriptedHttpMessageHandler.OrdinaryCard());
        using var app = new GameServiceApp(handler);
        using var client = app.CreateClient();

        await client.GetAsync("/draw");

        Assert.Equal(2, handler.CallCount);
        var keys = handler.Calls.Select(c => c.IdempotencyKey).ToList();
        Assert.All(keys, k => Assert.False(string.IsNullOrEmpty(k)));
        Assert.Single(keys.Distinct()); // same key on every attempt of this one draw
    }

    [Fact]
    public async Task Draw_DifferentDraws_UseDifferentIdempotencyKeys()
    {
        var handler = new ScriptedHttpMessageHandler(ScriptedHttpMessageHandler.OrdinaryCard());
        using var app = new GameServiceApp(handler);
        using var client = app.CreateClient();

        await client.GetAsync("/draw");
        await client.GetAsync("/draw");

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(2, handler.Calls.Select(c => c.IdempotencyKey).Distinct().Count());
    }

    [Fact]
    public async Task Draw_Failure_ResponseHasTraceIdAndLeaksNoExceptionDetail()
    {
        const string secret = "SUPERSECRET-exception-text-42";
        var handler = new ScriptedHttpMessageHandler(
            ScriptedHttpMessageHandler.Throw(new HttpRequestException(secret)));
        using var app = new GameServiceApp(handler);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/draw");
        var body = await response.Content.ReadAsStringAsync();

        using var json = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        Assert.DoesNotContain(secret, body); // neither message nor error leaks the exception text
    }

    [Fact]
    public async Task HealthLive_Returns200WithoutCallingCardService()
    {
        var handler = new ScriptedHttpMessageHandler(ScriptedHttpMessageHandler.OrdinaryCard());
        using var app = new GameServiceApp(handler);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, handler.CallCount);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetString();
    }
}
