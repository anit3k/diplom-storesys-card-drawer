using CardService;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CardService.UnitTests;

/// <summary>
/// Hand-made deterministic random source (no mocking library). Returns the supplied indices in
/// order and records every upper bound it was asked for, so tests can assert the draw picked an
/// index in [0, count).
/// </summary>
internal sealed class FakeRandomSource : IRandomSource
{
    private readonly Queue<int> indices;

    public FakeRandomSource(params int[] indices) => this.indices = new Queue<int>(indices);

    public List<int> RequestedMaxExclusive { get; } = new();

    public int Next(int maxExclusive)
    {
        this.RequestedMaxExclusive.Add(maxExclusive);
        return this.indices.Count > 0 ? this.indices.Dequeue() : 0;
    }
}

/// <summary>Hand-made event store fake that just records what was raised.</summary>
internal sealed class FakeEventStore : IEventStore
{
    public List<object> Raised { get; } = new();

    public void Raise(string eventName, object content) => this.Raised.Add(content);

    public IEnumerable<Event> GetEvents(long first, long last) => Array.Empty<Event>();
}

/// <summary>
/// In-process host for the real CardService (Microsoft.AspNetCore.Mvc.Testing). Each test news up
/// its own instance, so nothing is shared between tests. An optional deterministic random source
/// is swapped in via test services.
/// </summary>
internal sealed class CardServiceApp : WebApplicationFactory<Program>
{
    private readonly IRandomSource? random;

    public CardServiceApp(IRandomSource? random = null) => this.random = random;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (this.random is not null)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRandomSource>();
                services.AddSingleton(this.random);
            });
        }
    }
}
