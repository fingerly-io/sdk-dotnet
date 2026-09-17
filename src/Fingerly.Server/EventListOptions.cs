using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Fingerly;

/// <summary>
/// Filters for <see cref="EventsService.ListAsync"/>.
/// </summary>
/// <remarks>
/// An unset filter is not sent, so the server applies its own default.
/// <code>
/// var page = await fingerly.Events.ListAsync(
///     new EventListOptions { Level = "high", Limit = 50 });
/// </code>
/// </remarks>
public sealed class EventListOptions
{
    /// <summary>
    /// The API accepts RFC 3339 only, and reads it as an absolute instant.
    /// </summary>
    private const string Rfc3339 = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>
    /// Start of the window. Defaults to 24 hours ago; it may be at most 30
    /// days wide and start at most 30 days ago.
    /// </summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>End of the window. Defaults to now.</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>One-based page number.</summary>
    public int? Page { get; set; }

    /// <summary>Rows per page. The server caps it at 200.</summary>
    public int? Limit { get; set; }

    /// <summary>Restricts the listing to one exact visitor id.</summary>
    public string? Visitor { get; set; }

    /// <summary>Restricts it to <c>low</c>, <c>medium</c> or <c>high</c>.</summary>
    public string? Level { get; set; }

    /// <summary>
    /// Renders the filters as a URL query, omitting every unset one.
    /// </summary>
    /// <returns>The query including its leading <c>?</c>, or an empty string.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A page or limit is not positive. Checked here rather than in the
    /// setters, so an object initializer stays an object initializer.
    /// </exception>
    internal string ToQuery()
    {
        var parts = new List<string>(6);
        if (From.HasValue)
        {
            parts.Add("from=" + Uri.EscapeDataString(Format(From.Value)));
        }
        if (To.HasValue)
        {
            parts.Add("to=" + Uri.EscapeDataString(Format(To.Value)));
        }
        if (Page.HasValue)
        {
            parts.Add("page=" + Positive(Page.Value, nameof(Page)));
        }
        if (Limit.HasValue)
        {
            parts.Add("limit=" + Positive(Limit.Value, nameof(Limit)));
        }
        if (Visitor is not null)
        {
            parts.Add("visitor=" + Uri.EscapeDataString(Visitor));
        }
        if (Level is not null)
        {
            parts.Add("level=" + Uri.EscapeDataString(Level));
        }
        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var query = new StringBuilder("?");
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                query.Append('&');
            }
            query.Append(parts[i]);
        }
        return query.ToString();
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(Rfc3339, CultureInfo.InvariantCulture);

    private static string Positive(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, name + " must be positive");
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
