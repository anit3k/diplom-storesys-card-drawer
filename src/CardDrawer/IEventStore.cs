namespace CardDrawer;

public interface IEventStore
{
    void Raise(string eventName, object content);
    IEnumerable<Event> GetEvents(long firstEventSequenceNumber, long lastEventSequenceNumber);
}