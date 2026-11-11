using System.Text;
using GameService;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GameService.UnitTests;

public class OptionsValidationTests
{
    private static string AllMessages(Exception? exception)
    {
        var builder = new StringBuilder();
        for (var e = exception; e is not null; e = e.InnerException)
        {
            builder.AppendLine(e.Message);
        }

        return builder.ToString();
    }

    private static Exception? StartWith(Dictionary<string, string?> overrides)
    {
        using var handler = new ScriptedHttpMessageHandler(ScriptedHttpMessageHandler.OrdinaryCard());
        using var app = new GameServiceApp(handler, overrides);
        return Record.Exception(() => app.CreateClient()); // CreateClient starts the host -> ValidateOnStart runs
    }

    [Fact]
    public void Startup_WithRealDefaultConfiguration_Succeeds()
    {
        // The real appsettings defaults (no test overrides) must pass validation.
        using var app = new WebApplicationFactory<Program>();

        var exception = Record.Exception(() => app.CreateClient());

        Assert.Null(exception);
    }

    [Fact]
    public void Startup_AttemptTimeoutNotLessThanTotalTimeout_FailsWithClearMessage()
    {
        var exception = StartWith(new()
        {
            ["CardService:Resilience:AttemptTimeout"] = "00:00:02",
            ["CardService:Resilience:TotalTimeout"] = "00:00:01",
        });

        Assert.NotNull(exception);
        Assert.Contains("TotalTimeout", AllMessages(exception));
    }

    [Fact]
    public void Startup_FailureRatioZero_FailsWithClearMessage()
    {
        var exception = StartWith(new() { ["CardService:Resilience:FailureRatio"] = "0" });

        Assert.NotNull(exception);
        Assert.Contains("FailureRatio", AllMessages(exception));
    }

    [Fact]
    public void Startup_MinimumThroughputOne_FailsWithClearMessage()
    {
        var exception = StartWith(new() { ["CardService:Resilience:MinimumThroughput"] = "1" });

        Assert.NotNull(exception);
        Assert.Contains("MinimumThroughput", AllMessages(exception));
    }

    [Fact]
    public void Startup_RetryBudgetExceedsTotalTimeout_FailsWithClearMessage()
    {
        // 11 attempts x 500ms + back-off >> 1s total.
        var exception = StartWith(new()
        {
            ["CardService:Resilience:AttemptTimeout"] = "00:00:00.500",
            ["CardService:Resilience:MaxRetryAttempts"] = "10",
            ["CardService:Resilience:TotalTimeout"] = "00:00:01",
        });

        Assert.NotNull(exception);
        Assert.Contains("worst case", AllMessages(exception));
    }
}
