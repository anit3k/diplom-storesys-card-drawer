namespace GameService;

/// <summary>
/// Maps an exception to the HTTP status, stable error code and user-facing (Danish) message
/// GameService should return. A PURE function (no HttpContext, no logging, no I/O), so it can
/// be unit-tested directly without building the pipeline. The messages never contain internal
/// detail (exception text, URLs, hosts); that is logged server-side only.
/// </summary>
public static class CardServiceErrorMapping
{
    public record ErrorResult(int StatusCode, string Error, string Message);

    public static ErrorResult Map(Exception exception) => exception switch
    {
        CardServiceException { Failure: CardServiceFailure.Timeout } => new ErrorResult(
            504, "card_service_timeout",
            "Kortservicen svarede ikke i tide. Prøv igen om lidt."),

        CardServiceException { Failure: CardServiceFailure.Unavailable } => new ErrorResult(
            503, "card_service_unavailable",
            "Kortservicen er midlertidigt utilgængelig. Prøv igen om lidt."),

        CardServiceException { Failure: CardServiceFailure.CircuitOpen } => new ErrorResult(
            503, "card_service_circuit_open",
            "Tjenesten er midlertidigt lukket for nye forsøg. Prøv igen om lidt."),

        // 500/other non-503 5xx/4xx/invalid body from CardService. We return 502 (not 500) so
        // the caller knows the fault is in a dependency, not in GameService itself.
        CardServiceException { Failure: CardServiceFailure.BadResponse } => new ErrorResult(
            502, "card_service_bad_response",
            "Kortservicen gav et uventet svar."),

        // Anything else is an unexpected fault in GameService itself.
        _ => new ErrorResult(
            500, "internal_error",
            "Der opstod en uventet fejl."),
    };
}
