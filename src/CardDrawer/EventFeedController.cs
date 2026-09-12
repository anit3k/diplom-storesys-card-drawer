using Microsoft.AspNetCore.Mvc;

namespace CardDrawer;

[ApiController]
[Route("/events")]
public class EventFeedController : ControllerBase
{
    private readonly IEventStore eventStore;

    public EventFeedController(IEventStore eventStore)
    {
        this.eventStore = eventStore;
    }

    [HttpGet("")]
    public ActionResult<Event[]> Get([FromQuery] long start = 0, [FromQuery] long end = long.MaxValue) =>
        this.eventStore.GetEvents(start, end).ToArray();
}