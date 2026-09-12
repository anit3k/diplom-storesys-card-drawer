namespace CardDrawer;

public class EventStore : IEventStore
{
    private readonly List<Event> events = new();
    private readonly object lockObj = new();
    private long sequenceNumber;

    public void Raise(string eventName, object content)
    {
        lock (this.lockObj)
        {
            this.sequenceNumber++;
            this.events.Add(new Event(this.sequenceNumber, DateTimeOffset.UtcNow, eventName, content));
        }
    }

    public IEnumerable<Event> GetEvents(long firstEventSequenceNumber, long lastEventSequenceNumber) =>
        this.events
            .Where(e => e.SequenceNumber >= firstEventSequenceNumber && e.SequenceNumber <= lastEventSequenceNumber)
            .OrderBy(e => e.SequenceNumber);
}