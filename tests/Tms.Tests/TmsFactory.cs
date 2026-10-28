using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tms.Api.Data;
using Tms.Api.Modules.Ai;

namespace Tms.Tests;

/// <summary>Runs the real API against a throwaway SQLite database seeded with demo data.</summary>
public class TmsFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tms-test-{Guid.NewGuid():N}.db");
    public FakeLlm Llm { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = $"Data Source={_dbPath}",
            ["Auth:JwtKey"] = "test-signing-key-0123456789abcdef-0123456789",
            ["Auth:RequestsPerMinute"] = "10000",
            ["App:SeedDemoData"] = "true",
            ["Payments:Sandbox:Enabled"] = "true",
            ["Payments:Sandbox:WebhookSecret"] = "test-secret",
            ["Commerce:PayoutMinimumMinor"] = "100",
            ["Jitsi:Domain"] = "meet.test.local",
            ["Reminders:Enabled"] = "false", // tests run the reminder agent explicitly
            ["Jitsi:AppId"] = "tms-test",
            ["Jitsi:AppSecret"] = "jitsi-test-secret-0123456789abcdef",
        }));
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<ILlmClient>();
            s.AddSingleton<ILlmClient>(Llm);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    public async Task<HttpClient> LoginAsync(string email)
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = DemoSeed.Password });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public T Service<T>() where T : notnull => Services.CreateScope().ServiceProvider.GetRequiredService<T>();
}

public class FakeLlm : ILlmClient
{
    public List<(string System, IReadOnlyList<ChatTurn> Turns)> Calls { get; } = [];
    public string Reply { get; set; } = "It means rows with missing values (Lesson: Cleaning: missing values and duplicates).";
    /// <summary>Scripted replies for multi-call flows (e.g. the AI interviewer); falls back to <see cref="Reply"/> when empty.</summary>
    public Queue<string> Replies { get; } = new();

    public Task<ClaudeReply> CompleteAsync(string system, IReadOnlyList<ChatTurn> turns, IReadOnlyDictionary<string, JsonElement>? jsonSchema, CancellationToken ct)
    {
        Calls.Add((system, turns));
        return Task.FromResult(new ClaudeReply(Replies.Count > 0 ? Replies.Dequeue() : Reply, false, 100, 20));
    }
}

public static class Json
{
    public static async Task<JsonNode> ReadAsync(this HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    public static async Task<JsonNode> GetJsonAsync(this HttpClient c, string url) => await (await c.GetAsync(url)).ReadAsync();
    public static async Task<JsonNode> PostJsonAsync(this HttpClient c, string url, object body) => await (await c.PostAsJsonAsync(url, body)).ReadAsync();
}
