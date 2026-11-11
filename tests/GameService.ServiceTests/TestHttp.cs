using System.Text.Json;

namespace GameService.ServiceTests;

internal static class TestHttp
{
    /// <summary>Reads the "error" code from a GameService error body (null if absent).</summary>
    public static async Task<string?> ErrorCodeAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }
}
