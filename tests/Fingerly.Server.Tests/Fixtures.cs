using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Fingerly.Tests;

/// <summary>One recorded API response from the contract snapshot.</summary>
internal sealed record ResponseFixture(string Name, string Schema, int Status, JsonElement Body)
{
    internal string Json => Body.GetRawText();
}

/// <summary>One recorded webhook delivery from the contract snapshot.</summary>
internal sealed record WebhookFixture(
    string Name, string Secret, string Payload, string Timestamp, string Signature, bool Valid);

/// <summary>
/// The vendored server contract snapshot.
/// </summary>
/// <remarks>
/// It keeps the test suite independent of sibling repositories. Refresh it
/// with <c>dotnet run --project tools/SyncContract</c>.
/// </remarks>
internal static class Fixtures
{
    /// <summary>Where the snapshot lives, as the build recorded it.</summary>
    internal static readonly string ContractDirectory = Path.GetFullPath(
        typeof(Fixtures).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Fingerly.ContractDir")
            .Value!);

    private static readonly Lazy<JsonDocument> Document = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ContractDirectory, "fixtures.json"))));

    internal static IReadOnlyList<ResponseFixture> Responses(string schema) =>
        Document.Value.RootElement.GetProperty("responses").EnumerateArray()
            .Where(item => item.GetProperty("schema").GetString() == schema)
            .Select(item => new ResponseFixture(
                item.GetProperty("name").GetString()!,
                item.GetProperty("schema").GetString()!,
                item.GetProperty("status").GetInt32(),
                item.GetProperty("body").Clone()))
            .ToList();

    internal static IReadOnlyList<WebhookFixture> Webhooks() =>
        Document.Value.RootElement.GetProperty("webhooks").EnumerateArray()
            .Select(item => new WebhookFixture(
                item.GetProperty("name").GetString()!,
                item.GetProperty("secret").GetString()!,
                item.GetProperty("payload").GetString()!,
                item.GetProperty("timestamp").GetString()!,
                item.GetProperty("signature").GetString()!,
                item.GetProperty("valid").GetBoolean()))
            .ToList();
}
