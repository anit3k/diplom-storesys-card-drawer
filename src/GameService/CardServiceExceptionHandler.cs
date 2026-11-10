using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace GameService;

/// <summary>
/// Central exception handling (registered with AddExceptionHandler + UseExceptionHandler).
/// Maps every exception to the status/error/message via the pure <see cref="CardServiceErrorMapping"/>,
/// logs the full detail server-side, and writes the { error, message, traceId } body. Keeps all
/// HTTP error concerns in one place so controllers stay thin and free of try/catch.
/// </summary>
public class CardServiceExceptionHandler : IExceptionHandler
{
    private readonly ILogger<CardServiceExceptionHandler> logger;

    public CardServiceExceptionHandler(ILogger<CardServiceExceptionHandler> logger)
    {
        this.logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Caller cancellation (the client went away): not an error, send no error body.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            this.logger.LogInformation("Draw request was cancelled by the caller.");
            return true;
        }

        var result = CardServiceErrorMapping.Map(exception);

        // Log the full detail (including the exception) server-side only. An unexpected internal
        // error is an Error; a known downstream failure is a Warning.
        if (result.StatusCode >= 500 && result.Error == "internal_error")
        {
            this.logger.LogError(exception, "Unexpected error while drawing a card.");
        }
        else
        {
            this.logger.LogWarning(
                exception, "Draw failed: {Error} -> HTTP {Status}.", result.Error, result.StatusCode);
        }

        // Stable trace id for the response. A shared trace id across both services comes later.
        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = result.StatusCode;
        // NOTE: no Retry-After header — BrokenCircuitException exposes no RetryAfter value in
        // the installed Polly version (verified), so there is nothing authoritative to send.

        await httpContext.Response.WriteAsJsonAsync(
            new ErrorResponse(result.Error, result.Message, traceId), cancellationToken);

        return true;
    }
}
