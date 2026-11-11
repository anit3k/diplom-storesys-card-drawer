using System.Net;
using GameService;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace GameService.UnitTests;

public class CardServiceClientTests
{
    private static CardServiceClient ClientFor(ScriptedHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://cardservice.test") });

    [Fact]
    public async Task GetRandomCard_OrdinaryCard_ReturnsCard()
    {
        var client = ClientFor(new ScriptedHttpMessageHandler(
            ScriptedHttpMessageHandler.OrdinaryCard("Spades", "Ace")));

        var card = await client.GetRandomCardAsync("key", CancellationToken.None);

        Assert.Equal("Spades", card.Suit);
        Assert.Equal("Ace", card.Rank);
        Assert.False(card.IsJoker);
    }

    [Fact]
    public async Task GetRandomCard_Joker_ReturnsJoker()
    {
        var client = ClientFor(new ScriptedHttpMessageHandler(ScriptedHttpMessageHandler.Joker()));

        var card = await client.GetRandomCardAsync("key", CancellationToken.None);

        Assert.Null(card.Suit);
        Assert.Null(card.Rank);
        Assert.True(card.IsJoker);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)] // 500
    [InlineData(HttpStatusCode.NotFound)]            // 404
    [InlineData(HttpStatusCode.BadRequest)]          // 400
    public async Task GetRandomCard_UnexpectedStatus_ThrowsBadResponse(HttpStatusCode status)
    {
        var client = ClientFor(new ScriptedHttpMessageHandler(ScriptedHttpMessageHandler.Respond(status)));

        var ex = await Assert.ThrowsAsync<CardServiceException>(
            () => client.GetRandomCardAsync("key", CancellationToken.None));
        Assert.Equal(CardServiceFailure.BadResponse, ex.Failure);
    }

    [Fact]
    public async Task GetRandomCard_503_ThrowsUnavailable()
    {
        var client = ClientFor(new ScriptedHttpMessageHandler(
            ScriptedHttpMessageHandler.Respond(HttpStatusCode.ServiceUnavailable)));

        var ex = await Assert.ThrowsAsync<CardServiceException>(
            () => client.GetRandomCardAsync("key", CancellationToken.None));
        Assert.Equal(CardServiceFailure.Unavailable, ex.Failure);
    }

    [Fact]
    public async Task GetRandomCard_InvalidJsonBody_ThrowsBadResponse()
    {
        var client = ClientFor(new ScriptedHttpMessageHandler(
            ScriptedHttpMessageHandler.Respond(HttpStatusCode.OK, "{ not valid json ")));

        var ex = await Assert.ThrowsAsync<CardServiceException>(
            () => client.GetRandomCardAsync("key", CancellationToken.None));
        Assert.Equal(CardServiceFailure.BadResponse, ex.Failure);
    }

    [Fact]
    public async Task GetRandomCard_HttpRequestException_ThrowsUnavailable()
    {
        var client = ClientFor(new ScriptedHttpMessageHandler(
            ScriptedHttpMessageHandler.Throw(new HttpRequestException("connection refused"))));

        var ex = await Assert.ThrowsAsync<CardServiceException>(
            () => client.GetRandomCardAsync("key", CancellationToken.None));
        Assert.Equal(CardServiceFailure.Unavailable, ex.Failure);
    }

    [Fact]
    public async Task GetRandomCard_TimeoutRejectedException_ThrowsTimeout()
    {
        var client = ClientFor(new ScriptedHttpMessageHandler(
            ScriptedHttpMessageHandler.Throw(new TimeoutRejectedException("attempt timed out"))));

        var ex = await Assert.ThrowsAsync<CardServiceException>(
            () => client.GetRandomCardAsync("key", CancellationToken.None));
        Assert.Equal(CardServiceFailure.Timeout, ex.Failure);
    }

    [Fact]
    public async Task GetRandomCard_BrokenCircuitException_ThrowsCircuitOpen()
    {
        var client = ClientFor(new ScriptedHttpMessageHandler(
            ScriptedHttpMessageHandler.Throw(new BrokenCircuitException("circuit open"))));

        var ex = await Assert.ThrowsAsync<CardServiceException>(
            () => client.GetRandomCardAsync("key", CancellationToken.None));
        Assert.Equal(CardServiceFailure.CircuitOpen, ex.Failure);
    }
}
