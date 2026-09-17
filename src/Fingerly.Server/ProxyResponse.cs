using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Fingerly;

/// <summary>
/// One response for an adapter to write out.
/// </summary>
/// <remarks>
/// Headers are a list rather than a dictionary, because a response may
/// legitimately repeat a name and collapsing those would lose values.
/// </remarks>
public sealed class ProxyResponse
{
    private readonly byte[] _body;

    internal ProxyResponse(int status, IReadOnlyList<KeyValuePair<string, string>> headers, byte[] body)
    {
        Status = status;
        Headers = headers;
        _body = body;
    }

    /// <summary>The HTTP status code.</summary>
    public int Status { get; }

    /// <summary>The response headers, in order, possibly repeating a name.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    /// <summary>The body bytes. Empty for a status that must not carry one.</summary>
    public ReadOnlyMemory<byte> Body => _body;

    /// <summary>Writes the body into a stream.</summary>
    /// <param name="destination">Where to write it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once the body is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
    public Task WriteBodyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }
        return _body.Length == 0
            ? Task.CompletedTask
            : destination.WriteAsync(_body, 0, _body.Length, cancellationToken);
    }

    /// <summary>Builds a plain-text response that never reached Fingerly.</summary>
    internal static ProxyResponse Refusal(int status, string message)
    {
        var headers = new List<KeyValuePair<string, string>>(2)
        {
            new("Content-Type", "text/plain; charset=utf-8"),
            new("Cache-Control", "no-store"),
        };
        return new ProxyResponse(status, headers, Encoding.UTF8.GetBytes(message));
    }
}
