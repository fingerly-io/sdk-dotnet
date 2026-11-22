using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Fingerly;

/// <summary>
/// One request your server served, as raw input for
/// <see cref="AgentsService.ReportSightingsAsync"/>.
/// </summary>
/// <remarks>
/// Every field is what the request carried, unchanged: the server decides what
/// sent it. Nothing else about the request belongs here, never its path, query
/// string, cookies, <c>Authorization</c> or body.
/// </remarks>
public sealed class AgentSighting
{
    /// <summary>The <c>User-Agent</c> header as received, or empty when it had none. At most 4096 bytes.</summary>
    [JsonPropertyName("user_agent")]
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    /// The visitor's address from a trusted source: one IPv4 or IPv6 literal,
    /// never a forwarded list. Required.
    /// </summary>
    [JsonPropertyName("client_ip")]
    public string ClientIp { get; set; } = string.Empty;

    /// <summary>The <c>Host</c> the request was sent to, or null.</summary>
    [JsonPropertyName("host")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Host { get; set; }

    /// <summary>The request method, or null for <c>GET</c>.</summary>
    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; set; }

    /// <summary>The <c>Signature</c> header, when the request had one. At most 4096 bytes.</summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; set; }

    /// <summary>The <c>Signature-Input</c> header, when the request had one. At most 4096 bytes.</summary>
    [JsonPropertyName("signature_input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignatureInput { get; set; }

    /// <summary>The <c>Signature-Agent</c> header, when the request had one. At most 4096 bytes.</summary>
    [JsonPropertyName("signature_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignatureAgent { get; set; }

    /// <summary>
    /// How many times this exact request was served since it was last
    /// reported, this one included: 1 to 1,000,000.
    /// </summary>
    [JsonPropertyName("count")]
    public long Count { get; set; } = 1;

    /// <summary>Names the method and count; the headers and address are left out.</summary>
    /// <returns>A description safe to log.</returns>
    public override string ToString() => "AgentSighting { Method = " + (Method ?? "GET") + ", Count = " + Count + " }";
}

/// <summary>
/// The server's answer to a report: one <see cref="AgentAnswer"/> per
/// reported request, in the order they were sent.
/// </summary>
public sealed class AgentSightingsResult
{
    private readonly string? _rulesVersion;
    private readonly IReadOnlyList<AgentAnswer>? _items;

    /// <summary>
    /// Names the classification rules. When it changes, every remembered
    /// answer is out of date. Never null; empty when the answer named none.
    /// </summary>
    [JsonPropertyName("rules_version")]
    public string RulesVersion
    {
        get => _rulesVersion ?? string.Empty;
        init => _rulesVersion = value;
    }

    /// <summary>
    /// One answer per reported request, in the same order. Never null, and
    /// never holds null: an answer the API sent as <c>null</c> reads as
    /// <see cref="AgentAnswer.Unknown"/>, so the positions still line up.
    /// </summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<AgentAnswer> Items
    {
        get => _items ?? [];
        init => _items = WithoutNulls(value);
    }

    /// <summary>The list with every null replaced by <see cref="AgentAnswer.Unknown"/>.</summary>
    private static IReadOnlyList<AgentAnswer>? WithoutNulls(IReadOnlyList<AgentAnswer>? items)
    {
        if (items is null)
        {
            return null;
        }
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is null)
            {
                var copy = new AgentAnswer[items.Count];
                for (int j = 0; j < copy.Length; j++)
                {
                    // The binder puts a JSON null here whatever the annotation says.
                    copy[j] = (AgentAnswer?)items[j] ?? AgentAnswer.Unknown;
                }
                return copy;
            }
        }
        return items;
    }
}

/// <summary>
/// What sent one request, as the server answered it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Unknown"/> stands for "no answer yet": the request was never
/// reported, its answer expired, or reporting is not working. Serve such a
/// request as usual.
/// </para>
/// <code>
/// AgentAnswer answer = sightings.Lookup(request);
/// if (answer.VisitorKind == VisitorKinds.AiBot &amp;&amp; answer.Ai?.Purpose == AiPurposes.Training)
/// {
///     // Your own policy for training crawlers.
/// }
/// </code>
/// </remarks>
public sealed class AgentAnswer
{
    /// <summary>
    /// The answer for a request nobody has answered yet. Its
    /// <see cref="VisitorKind"/> is empty, which matches none of
    /// <see cref="VisitorKinds"/>, so a check against them fails open.
    /// </summary>
    public static readonly AgentAnswer Unknown = new();

    private readonly string? _visitorKind;

    /// <summary>
    /// The seconds this answer may be reused for the same user agent, address
    /// and <c>Signature-Agent</c>; 0 for a signed request, which is checked
    /// every time.
    /// </summary>
    [JsonPropertyName("ttl")]
    public long Ttl { get; init; }

    /// <summary>
    /// <c>human</c>, <c>ai_bot</c>, <c>automation</c> or
    /// <c>search_crawler</c>; see <see cref="VisitorKinds"/>. The API can add
    /// values, so keep a default branch. Never null; empty for
    /// <see cref="Unknown"/> and for an answer that named no kind.
    /// </summary>
    [JsonPropertyName("visitor_kind")]
    public string VisitorKind
    {
        get => _visitorKind ?? string.Empty;
        init => _visitorKind = value;
    }

    /// <summary>The agent's name where the server knows it, such as <c>GPTBot</c> or <c>Googlebot</c>; null for a person's browser.</summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    /// <summary>
    /// Present when <see cref="VisitorKind"/> is <see cref="VisitorKinds.AiBot"/>:
    /// who operates the agent, what its traffic is for, and how far its claim
    /// was checked.
    /// </summary>
    [JsonPropertyName("ai")]
    public AiAgent? Ai { get; init; }

    /// <summary>True only for <see cref="Unknown"/>.</summary>
    [JsonIgnore]
    public bool IsUnknown => ReferenceEquals(this, Unknown);

    /// <inheritdoc/>
    public override string ToString() =>
        "AgentAnswer[visitorKind=" + VisitorKind + ", agent=" + Agent + ", ttl=" + Ttl + "]";
}

/// <summary>The body of a report.</summary>
internal sealed class AgentSightingsBody
{
    [JsonPropertyName("items")]
    public IReadOnlyList<AgentSighting> Items { get; init; } = [];
}
