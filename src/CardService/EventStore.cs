using System.Diagnostics;

namespace CardService;

public class EventStore : IEventStore
{
    private readonly List<Event> events = new();
    private readonly object lockObj = new();
    private long sequenceNumber;

    public void Raise(string eventName, object content)
    {
        // Capture the trace id at the moment the event is raised, so the feed shows which
        // request caused it. Null when there is no active Activity.
        var traceId = Activity.Current?.TraceId.ToString();

        lock (this.lockObj)
        {
            this.sequenceNumber++;
            this.events.Add(new Event(this.sequenceNumber, DateTimeOffset.UtcNow, eventName, content, traceId));
        }
    }

    public IEnumerable<Event> GetEvents(long firstEventSequenceNumber, long lastEventSequenceNumber) =>
        this.events
            .Where(e => e.SequenceNumber >= firstEventSequenceNumber && e.SequenceNumber <= lastEventSequenceNumber)
            .OrderBy(e => e.SequenceNumber);
}