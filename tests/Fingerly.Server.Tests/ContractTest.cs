using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Fingerly.Tests;

/// <summary>
/// Every recorded response and delivery, read through the public API.
/// </summary>
/// <remarks>
/// These are the tests that would fail if the server changed a field name or a
/// signing rule, rather than if this SDK changed its mind about one.
/// </remarks>
public class ContractTest
{
    private const string SecretKey = "fly_sk_production_contract";

    [Fact]
    public void SnapshotMatchesItsManifest()
    {
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(Fixtures.ContractDirectory, "manifest.json")));
        JsonElement files = manifest.RootElement.GetProperty("files");

        int checkedFiles = 0;
        foreach (JsonProperty file in files.EnumerateObject())
        {
            byte[] content = File.ReadAllBytes(Path.Combine(Fixtures.ContractDirectory, file.Name));
            string actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            Assert.Equal(file.Value.GetString(), actual);
            checkedFiles++;
        }
        Assert.NotEqual(0, checkedFiles);
    }

    [Fact]
    public async Task ReadsEveryEventFixture()
    {
        foreach (ResponseFixture fixture in Fixtures.Responses("SDKEventOut"))
        {
            await using TestApi api = await TestApi.RespondAsync(fixture.Status, fixture.Json);
            using FingerlyClient fingerly = Client(api);

            Event stored = await fingerly.Events.GetAsync("id");

            Assert.Equal(fixture.Body.GetProperty("request_id").GetString(), stored.RequestId);
            Assert.Equal(fixture.Body.GetProperty("visitor_id").GetString(), stored.VisitorId);
            Assert.NotEqual(default, stored.OccurredAt);
            Assert.Equal(ProjectIdOf(fixture.Body), stored.ProjectId);

            // A fixture without a score must not decode as zero.
            bool expected = fixture.Body.TryGetProperty("suspect_score", out JsonElement score)
                && score.ValueKind != JsonValueKind.Null;
            Assert.Equal(expected, stored.TryGetScore(out int value));
            if (expected)
            {
                Assert.Equal(score.GetInt32(), value);
            }

            Assert.Equal(
                fixture.Body.TryGetProperty("visitor_kind", out JsonElement kind) ? kind.GetString() : null,
                stored.VisitorKind);
            // ai is absent on every row that is not an AI agent.
            Assert.Equal(
                fixture.Body.TryGetProperty("ai", out JsonElement ai) && ai.ValueKind == JsonValueKind.Object,
                stored.Ai is not null);
        }
    }

    [Fact]
    public async Task ReadsTheAiAgentFixture()
    {
        ResponseFixture fixture = Named("ai_agent");
        await using TestApi api = await TestApi.RespondAsync(fixture.Status, fixture.Json);
        using FingerlyClient fingerly = Client(api);

        Event stored = await fingerly.Events.GetAsync("id");

        Assert.Equal(VisitorKinds.AiBot, stored.VisitorKind);
        AiAgent ai = Assert.IsType<AiAgent>(stored.Ai);
        Assert.Equal("ChatGPT-User", ai.Name);
        Assert.Equal("OpenAI", ai.Provider);
        Assert.Equal(AiPurposes.UserFetch, ai.Purpose);
        Assert.Equal(AiRuntimes.ProviderCloud, ai.Runtime);
        Assert.True(ai.Declared);
        AiVerification verification = Assert.IsType<AiVerification>(ai.Verification);
        Assert.Equal(AiVerificationMethods.None, verification.Method);
        Assert.Equal(AiVerificationResults.NotAttempted, verification.Result);
    }

    [Fact]
    public async Task ReadsAnEventWithoutAi()
    {
        ResponseFixture fixture = Named("event");
        Assert.False(fixture.Body.TryGetProperty("ai", out _));
        await using TestApi api = await TestApi.RespondAsync(fixture.Status, fixture.Json);
        using FingerlyClient fingerly = Client(api);

        Event stored = await fingerly.Events.GetAsync("id");

        Assert.Null(stored.Ai);
    }

    [Fact]
    public async Task ReadsAiValuesThisSdkDoesNotKnow()
    {
        string body = Named("ai_agent").Json
            .Replace("\"user_fetch\"", "\"future_purpose\"", StringComparison.Ordinal)
            .Replace("\"provider_cloud\"", "\"future_runtime\"", StringComparison.Ordinal)
            .Replace("\"none\"", "\"future_method\"", StringComparison.Ordinal)
            .Replace("\"not_attempted\"", "\"future_result\"", StringComparison.Ordinal)
            .Replace("\"ai_bot\"", "\"future_kind\"", StringComparison.Ordinal);
        await using TestApi api = await TestApi.RespondAsync(200, body);
        using FingerlyClient fingerly = Client(api);

        Event stored = await fingerly.Events.GetAsync("id");

        Assert.Equal("future_kind", stored.VisitorKind);
        AiAgent ai = Assert.IsType<AiAgent>(stored.Ai);
        Assert.Equal("future_purpose", ai.Purpose);
        Assert.Equal("future_runtime", ai.Runtime);
        Assert.Equal("future_method", ai.Verification?.Method);
        Assert.Equal("future_result", ai.Verification?.Result);
    }

    private static ResponseFixture Named(string name)
    {
        foreach (ResponseFixture fixture in Fixtures.Responses("SDKEventOut"))
        {
            if (fixture.Name == name)
            {
                return fixture;
            }
        }
        throw new InvalidOperationException("no fixture named " + name);
    }

    [Fact]
    public async Task ReadsEveryPageFixture()
    {
        foreach (ResponseFixture fixture in Fixtures.Responses("SDKEventsOut"))
        {
            await using TestApi api = await TestApi.RespondAsync(fixture.Status, fixture.Json);
            using FingerlyClient fingerly = Client(api);

            EventPage page = await fingerly.Events.ListAsync();

            Assert.Equal(fixture.Body.GetProperty("page").GetInt32(), page.Page);
            Assert.Equal(fixture.Body.GetProperty("page_size").GetInt32(), page.PageSize);

            List<JsonElement> rows = [.. fixture.Body.GetProperty("rows").EnumerateArray()];
            Assert.Equal(rows.Count, page.Rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                Assert.Equal(rows[i].GetProperty("request_id").GetString(), page.Rows[i].RequestId);
                Assert.Equal(ProjectIdOf(rows[i]), page.Rows[i].ProjectId);
            }
        }
    }

    [Fact]
    public async Task MapsEveryErrorFixture()
    {
        foreach (ResponseFixture fixture in Fixtures.Responses("ErrorResponse"))
        {
            await using TestApi api = await TestApi.RespondAsync(fixture.Status, fixture.Json);
            using FingerlyClient fingerly = Client(api);

            FingerlyApiException failure = await Assert.ThrowsAsync<FingerlyApiException>(
                () => fingerly.Events.GetAsync("id"));

            Assert.Equal(fixture.Status, failure.Status);
            Assert.Equal(
                fixture.Body.GetProperty("error").GetProperty("code").GetString(), failure.Code);
            Assert.Equal(fixture.Body.GetProperty("request_id").GetString(), failure.RequestId);
        }
    }

    [Fact]
    public void VerifiesEveryWebhookFixture()
    {
        IReadOnlyList<WebhookFixture> fixtures = Fixtures.Webhooks();
        Assert.NotEmpty(fixtures);

        foreach (WebhookFixture fixture in fixtures)
        {
            // Each is verified at the instant it was signed.
            DateTimeOffset signedAt = DateTimeOffset.FromUnixTimeSeconds(
                long.Parse(fixture.Timestamp, System.Globalization.CultureInfo.InvariantCulture));

            bool verified = Webhooks.Verify(
                fixture.Secret,
                Encoding.UTF8.GetBytes(fixture.Payload),
                fixture.Timestamp,
                fixture.Signature,
                now: signedAt);

            Assert.Equal(fixture.Valid, verified);
        }

        // The same fixture, past the replay window, must be refused.
        WebhookFixture stale = fixtures[0];
        DateTimeOffset tooLate = DateTimeOffset
            .FromUnixTimeSeconds(long.Parse(
                stale.Timestamp, System.Globalization.CultureInfo.InvariantCulture))
            .AddSeconds(301);
        Assert.False(Webhooks.Verify(
            stale.Secret,
            Encoding.UTF8.GetBytes(stale.Payload),
            stale.Timestamp,
            stale.Signature,
            now: tooLate));
    }

    [Fact]
    public void ParsesTheWebhookEnvelope()
    {
        WebhookFixture fixture = Fixtures.Webhooks()[0];

        WebhookEvent delivery = Webhooks.Parse(Encoding.UTF8.GetBytes(fixture.Payload));

        Assert.False(string.IsNullOrEmpty(delivery.Id));
        Assert.False(string.IsNullOrEmpty(delivery.Type));
        // Data stays raw, so a new event type needs no SDK upgrade.
        Assert.Equal(JsonValueKind.Object, delivery.Data.ValueKind);
        Assert.False(string.IsNullOrEmpty(
            delivery.Data.GetProperty("request_id").GetString()));
        // The project travels in data, read like any other member.
        Assert.False(string.IsNullOrEmpty(ProjectIdOf(delivery.Data)));
    }

    [Fact]
    public void OpenApiRetiresSupplementAndDeferredToken()
    {
        using JsonDocument openApi = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(Fixtures.ContractDirectory, "openapi.json")));
        JsonElement paths = openApi.RootElement.GetProperty("paths");

        Assert.True(paths.TryGetProperty("/api/v1/ingest", out _));
        foreach (JsonProperty route in paths.EnumerateObject())
        {
            Assert.DoesNotContain("supplement", route.Name, StringComparison.OrdinalIgnoreCase);
        }

        JsonElement schemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.False(schemas.TryGetProperty("SupplementOut", out _));
        JsonElement identifyProps = schemas.GetProperty("IdentifyOut").GetProperty("properties");
        Assert.False(identifyProps.TryGetProperty("deferred_token", out _));
        Assert.True(
            schemas.GetProperty("SDKEventOut").GetProperty("properties").TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task DecodesUnidentifiableEventAndAllV3TriggerSignals()
    {
        const string Payload = """
            {
              "request_id": "0199a963-e400-7000-8000-000000000099",
              "visitor_id": "",
              "occurred_at": "2026-01-02T03:04:05Z",
              "state": "unavailable",
              "reason": "unidentifiable_device",
              "suspect_score": 88,
              "suspect_level": "high",
              "triggers": [
                {"signal": "attestation_failed", "group": "attestation_failed", "weight": 40, "confidence": "high"},
                {"signal": "attestation_downgrade", "group": "attestation_failed", "weight": 30, "confidence": "high"},
                {"signal": "device_inconsistent", "weight": 20, "confidence": "high"},
                {"signal": "report_incomplete", "weight": 15, "confidence": "medium"},
                {"signal": "probe_errors", "group": "report_incomplete", "weight": 4, "confidence": "low"},
                {"signal": "fetch_metadata_missing", "group": "bot", "weight": 2, "confidence": "low"},
                {"signal": "web_ticket_missing", "group": "report_incomplete", "weight": 4, "confidence": "low"},
                {"signal": "web_ticket_invalid", "group": "browser_tampering", "weight": 20, "confidence": "high"},
                {"signal": "tampering", "weight": 25, "confidence": "high"},
                {"signal": "custom_root_ca", "weight": 30, "confidence": "high"},
                {"signal": "secondary_user_profile", "weight": 5, "confidence": "low"}
              ],
              "has_document": false
            }
            """;
        await using TestApi api = await TestApi.RespondAsync(200, Payload);
        using FingerlyClient fingerly = Client(api);

        Event stored = await fingerly.Events.GetAsync("0199a963-e400-7000-8000-000000000099");

        Assert.Equal(string.Empty, stored.VisitorId);
        Assert.Equal("unavailable", stored.State);
        Assert.Equal("unidentifiable_device", stored.Reason);
        Assert.Equal(88, stored.SuspectScore);
        Assert.Equal(11, stored.Triggers.Count);
        Assert.Equal("attestation_failed", stored.Triggers[0].Group);
        Assert.Equal("attestation_failed", stored.Triggers[1].Group);
        Assert.Equal("report_incomplete", stored.Triggers[4].Group);
        Assert.Equal("bot", stored.Triggers[5].Group);
        Assert.Equal("report_incomplete", stored.Triggers[6].Group);
        Assert.Equal("browser_tampering", stored.Triggers[7].Group);
        Assert.Equal("web_ticket_missing", EventTrigger.SignalWebTicketMissing);
        Assert.Equal("web_ticket_invalid", EventTrigger.SignalWebTicketInvalid);
        var actualSignals = new List<string>();
        foreach (EventTrigger trigger in stored.Triggers)
        {
            actualSignals.Add(trigger.Signal!);
        }
        Assert.Equal(EventTrigger.V3Signals, actualSignals);
    }

    private static string? ProjectIdOf(JsonElement body) =>
        body.TryGetProperty("project_id", out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static FingerlyClient Client(TestApi api) =>
        new(new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin, AllowInsecureLoopback = true });
}
