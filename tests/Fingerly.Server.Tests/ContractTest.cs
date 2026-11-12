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
        }
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

    private static string? ProjectIdOf(JsonElement body) =>
        body.TryGetProperty("project_id", out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static FingerlyClient Client(TestApi api) =>
        new(new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin });
}
