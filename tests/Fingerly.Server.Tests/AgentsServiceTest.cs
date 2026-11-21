using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Fingerly.Tests;

public class AgentsServiceTest
{
    private const string SecretKey = "fly_sk_production_agents";

    private static FingerlyClient Client(TestApi api) => new(new FingerlyOptions
    {
        SecretKey = SecretKey,
        Endpoint = api.Origin,
        AllowInsecureLoopback = true,
    });

    private static ResponseFixture Fixture() =>
        Fixtures.Responses("AgentSightingsOut").Single(fixture => fixture.Name == "agent_sightings");

    [Fact]
    public async Task PostsTheItemsWithTheSecretKey()
    {
        ResponseFixture fixture = Fixture();
        await using TestApi api = await TestApi.RespondAsync(200, fixture.Json);
        using FingerlyClient fingerly = Client(api);

        await fingerly.Agents.ReportSightingsAsync(
        [
            new AgentSighting { UserAgent = "GPTBot/1.4", ClientIp = "20.171.206.9", Host = "shop.example", Method = "GET", Count = 3 },
            new AgentSighting
            {
                UserAgent = "", ClientIp = "2001:db8::1", Method = "HEAD",
                Signature = "sig1=:abc:", SignatureInput = "sig1=()", SignatureAgent = "\"https://chatgpt.com\"",
            },
        ]);

        RecordedRequest sent = Assert.Single(api.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/api/v1/agents/sightings", sent.Path);
        Assert.Equal(SecretKey, sent.Header("X-API-Key"));
        Assert.StartsWith("application/json", sent.Header("Content-Type"));
        using JsonDocument body = JsonDocument.Parse(sent.Body);
        JsonElement[] items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(
            """{"user_agent":"GPTBot/1.4","client_ip":"20.171.206.9","host":"shop.example","method":"GET","count":3}""",
            items[0].GetRawText());
        // Absent headers are left out rather than sent as null.
        Assert.False(items[1].TryGetProperty("host", out _));
        Assert.Equal("", items[1].GetProperty("user_agent").GetString());
        Assert.Equal("sig1=:abc:", items[1].GetProperty("signature").GetString());
        Assert.Equal("sig1=()", items[1].GetProperty("signature_input").GetString());
        Assert.Equal("\"https://chatgpt.com\"", items[1].GetProperty("signature_agent").GetString());
        Assert.Equal(1, items[1].GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task ReadsTheAgentSightingsFixture()
    {
        ResponseFixture fixture = Fixture();
        await using TestApi api = await TestApi.RespondAsync(fixture.Status, fixture.Json);
        using FingerlyClient fingerly = Client(api);

        AgentSightingsResult result = await fingerly.Agents.ReportSightingsAsync(
            Enumerable.Range(0, 5).Select(_ => new AgentSighting { ClientIp = "203.0.113.7" }).ToList());

        Assert.Equal("0123456789abcdef", result.RulesVersion);
        Assert.Equal(5, result.Items.Count);

        AgentAnswer human = result.Items[0];
        Assert.Equal(3600, human.Ttl);
        Assert.Equal(VisitorKinds.Human, human.VisitorKind);
        Assert.Null(human.Agent);
        Assert.Null(human.Ai);
        Assert.False(human.IsUnknown);

        AgentAnswer gptBot = result.Items[1];
        Assert.Equal(900, gptBot.Ttl);
        Assert.Equal(VisitorKinds.AiBot, gptBot.VisitorKind);
        Assert.Equal("GPTBot", gptBot.Agent);
        AiAgent ai = Assert.IsType<AiAgent>(gptBot.Ai);
        Assert.Equal("OpenAI", ai.Provider);
        Assert.Equal(AiPurposes.Training, ai.Purpose);
        Assert.Equal(AiRuntimes.ProviderCloud, ai.Runtime);
        Assert.True(ai.Declared);
        Assert.Equal(AiVerificationMethods.IpList, ai.Verification!.Method);
        Assert.Equal(AiVerificationResults.Pass, ai.Verification.Result);

        Assert.Equal(AiVerificationResults.Fail, result.Items[2].Ai!.Verification!.Result);

        AgentAnswer signed = result.Items[3];
        Assert.Equal(0, signed.Ttl);
        Assert.Equal(AiPurposes.Agent, signed.Ai!.Purpose);
        Assert.Equal(AiVerificationMethods.Signature, signed.Ai.Verification!.Method);

        AgentAnswer googlebot = result.Items[4];
        Assert.Equal(VisitorKinds.SearchCrawler, googlebot.VisitorKind);
        Assert.Equal("Googlebot", googlebot.Agent);
        Assert.Null(googlebot.Ai);
    }

    [Fact]
    public async Task ReadsValuesThisSdkDoesNotKnow()
    {
        await using TestApi api = await TestApi.RespondAsync(200,
            """{"rules_version":"r2","items":[{"ttl":60,"visitor_kind":"future_kind","agent":"X","ai":{"purpose":"future","runtime":"future","verification":{"method":"future","result":"future"}},"later":true}],"later":1}""");
        using FingerlyClient fingerly = Client(api);

        AgentSightingsResult result = await fingerly.Agents.ReportSightingsAsync([new AgentSighting { ClientIp = "203.0.113.7" }]);

        Assert.Equal("future_kind", result.Items[0].VisitorKind);
        Assert.Equal("future", result.Items[0].Ai!.Purpose);
    }

    [Fact]
    public async Task MapsARateLimitWithItsRetryAfterAndScope()
    {
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            context.Response.StatusCode = 429;
            context.Response.Headers["Retry-After"] = "3600";
            context.Response.Headers["Fingerly-RateLimit-Scope"] = "daily";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"error":{"code":"rate_limited","status":429}}""");
        });
        using FingerlyClient fingerly = Client(api);

        FingerlyApiException error = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Agents.ReportSightingsAsync([new AgentSighting { ClientIp = "203.0.113.7" }]));

        Assert.Equal(429, error.Status);
        Assert.Equal("rate_limited", error.Code);
        Assert.Equal(3600, error.RetryAfter);
        Assert.Equal(RateLimitScopes.Daily, error.RateLimitScope);
    }

    [Fact]
    public async Task MapsAnInvalidRequest()
    {
        await using TestApi api = await TestApi.RespondAsync(422, """{"error":{"code":"invalid_request","status":422}}""");
        using FingerlyClient fingerly = Client(api);

        FingerlyApiException error = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Agents.ReportSightingsAsync([new AgentSighting { ClientIp = "203.0.113.7" }]));

        Assert.Equal(422, error.Status);
        Assert.Equal("invalid_request", error.Code);
        Assert.Null(error.RateLimitScope);
    }

    [Fact]
    public async Task RefusesAnEmptyOrOversizedReportWithoutSending()
    {
        await using TestApi api = await TestApi.RespondAsync(200, Fixture().Json);
        using FingerlyClient fingerly = Client(api);

        await Assert.ThrowsAsync<ArgumentException>(() => fingerly.Agents.ReportSightingsAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => fingerly.Agents.ReportSightingsAsync(
            Enumerable.Range(0, AgentsService.MaxItems + 1).Select(_ => new AgentSighting { ClientIp = "203.0.113.7" }).ToList()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => fingerly.Agents.ReportSightingsAsync(null!));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public void UnknownMatchesNoVisitorKind()
    {
        Assert.True(AgentAnswer.Unknown.IsUnknown);
        Assert.Equal("", AgentAnswer.Unknown.VisitorKind);
        Assert.Equal(0, AgentAnswer.Unknown.Ttl);
        Assert.Null(AgentAnswer.Unknown.Agent);
        Assert.Null(AgentAnswer.Unknown.Ai);
        Assert.False(new AgentAnswer().IsUnknown);
        Assert.DoesNotContain(AgentAnswer.Unknown.VisitorKind, new[]
        {
            VisitorKinds.Human, VisitorKinds.AiBot, VisitorKinds.Automation, VisitorKinds.SearchCrawler,
        });
    }
}
