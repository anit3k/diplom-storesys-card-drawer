using System.Text;
using System.Text.Json;
using GameService;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameService.UnitTests;

public class ErrorMappingTests
{
    [Theory]
    [InlineData(CardServiceFailure.Timeout, 504, "card_service_timeout")]
    [InlineData(CardServiceFailure.Unavailable, 503, "card_service_unavailable")]
    [InlineData(CardServiceFailure.CircuitOpen, 503, "card_service_circuit_open")]
    [InlineData(CardServiceFailure.BadResponse, 502, "card_service_bad_response")]
    public void Map_CardServiceFailure_ReturnsContractStatusAndError(
        CardServiceFailure failure, int expectedStatus, string expectedError)
    {
        var result = CardServiceErrorMapping.Map(new CardServiceException(failure, "ignored internal detail"));

        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.Equal(expectedError, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void Map_UnknownException_Returns500InternalError()
    {
        var result = CardServiceErrorMapping.Map(new InvalidOperationException("boom"));

        Assert.Equal(500, result.StatusCode);
        Assert.Equal("internal_error", result.Error);
    }

    [Fact]
    public void Map_Message_DoesNotContainInternalExceptionDetail()
    {
        const string secret = "SECRET-internal-detail-7f3a";

        foreach (var failure in Enum.GetValues<CardServiceFailure>())
        {
            var result = CardServiceErrorMapping.Map(
                new CardServiceException(failure, secret, new Exception(secret)));
            Assert.DoesNotContain(secret, result.Message);
            Assert.DoesNotContain(secret, result.Error);
        }

        var unknown = CardServiceErrorMapping.Map(new InvalidOperationException(secret));
        Assert.DoesNotContain(secret, unknown.Message);
    }

    [Fact]
    public async Task ExceptionHandler_CircuitOpen_WritesBodyAndSetsNoRetryAfterHeader()
    {
        // Our handler never sets Retry-After: BrokenCircuitException exposes no retry-after value
        // in the installed Polly version (see ADR 0007), so there is no "present" branch to test.
        var handler = new CardServiceExceptionHandler(NullLogger<CardServiceExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(
            context, new CardServiceException(CardServiceFailure.CircuitOpen, "open"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(503, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));

        context.Response.Body.Position = 0;
        using var json = JsonDocument.Parse(await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync());
        Assert.Equal("card_service_circuit_open", json.RootElement.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
    }
}
