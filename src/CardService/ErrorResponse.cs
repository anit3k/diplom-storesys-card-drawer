namespace CardService;

/// <summary>
/// Shared error shape from docs/api-contract.md. Serialized as { error, message, traceId }
/// (ASP.NET Core camel-cases the property names).
/// </summary>
public record ErrorResponse(string Error, string Message, string TraceId);
