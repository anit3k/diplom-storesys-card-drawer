namespace CardService;

/// <summary>
/// An event in the feed. <see cref="TraceId"/> is the W3C trace id captured when the event was
/// raised (from Activity.Current), so a later reader can see which request caused it; it is null
/// when no trace context was active. The other fields are unchanged.
/// </summary>
public record Event(long SequenceNumber, DateTimeOffset OccurredAt, string Name, object Content, string? TraceId);