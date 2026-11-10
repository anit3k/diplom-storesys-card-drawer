namespace GameService;

/// <summary>
/// The error body GameService returns (see docs/api-contract.md). Serialized as
/// { error, message, traceId } (ASP.NET Core camel-cases the property names).
/// Never contains exception text, URLs, host/port names or stack traces — those are logged
/// server-side only.
/// </summary>
public record ErrorResponse(string Error, string Message, string TraceId);
