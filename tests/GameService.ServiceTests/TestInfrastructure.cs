using GameService;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace GameService.ServiceTests;

/// <summary>
/// Runs GameService in-process with its REAL HTTP pipeline (no handler is replaced), pointed at a
/// <see cref="MockCardService"/>. Small real-time resilience values keep the suite fast with big
/// margins. Each test news up its own instance (and its own mock), so there is no shared state.
/// </summary>
internal sealed class GameServiceApp : WebApplicationFactory<Program>
{
    private readonly string cardServiceBaseUrl;

    public GameServiceApp(string cardServiceBaseUrl) => this.cardServiceBaseUrl = cardServiceBaseUrl;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CardService:BaseUrl"] = this.cardServiceBaseUrl,
                ["CardService:Resilience:AttemptTimeout"] = "00:00:00.100",
                ["CardService:Resilience:TotalTimeout"] = "00:00:01",
                ["CardService:Resilience:MaxRetryAttempts"] = "2",
                ["CardService:Resilience:RetryBaseDelay"] = "00:00:00.010",
                ["CardService:Resilience:UseJitter"] = "false",
                ["CardService:Resilience:FailureRatio"] = "0.5",
                ["CardService:Resilience:MinimumThroughput"] = "6",
                ["CardService:Resilience:SamplingDuration"] = "00:00:10",
                ["CardService:Resilience:BreakDuration"] = "00:00:01",
            }));
    }
}

internal static class Poll
{
    /// <summary>
    /// Polls <paramref name="condition"/> until it is true or <paramref name="timeout"/> elapses
    /// (no fixed sleeps in the tests). Returns whether the condition became true.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> condition, TimeSpan timeout, TimeSpan? interval = null)
    {
        var step = interval ?? TimeSpan.FromMilliseconds(50);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(step);
        }

        return await condition();
    }
}
