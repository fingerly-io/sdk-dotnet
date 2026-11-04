using System;
using System.Globalization;
using System.Text.Json;

namespace Fingerly;

/// <summary>
/// Base class for every exception this SDK raises.
/// </summary>
public class FingerlyException : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public FingerlyException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an underlying cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The failure this one wraps.</param>
    public FingerlyException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown for any non-2xx response from the Fingerly API.
/// </summary>
/// <remarks>
/// <para>
/// The message carries only the HTTP status. Response bodies are never echoed,
/// and any occurrence of the secret key in the metadata is redacted.
/// </para>
/// <code>
/// try
/// {
///     ev = await fingerly.Events.GetAsync(requestId, ct);
/// }
/// catch (FingerlyApiException e) when (e.Status == 404)
/// {
///     return "refuse";
/// }
/// </code>
/// </remarks>
public class FingerlyApiException : FingerlyException
{
    internal FingerlyApiException(
        int status, string? code, JsonElement? details, string? requestId, int? retryAfter = null)
        : base(Describe(status, code))
    {
        Status = status;
        Code = code;
        Details = details;
        RequestId = requestId;
        RetryAfter = retryAfter;
    }

    /// <summary>Creates an exception for a status the API reported.</summary>
    /// <param name="status">The HTTP status code.</param>
    public FingerlyApiException(int status)
        : this(status, null, null, null)
    {
    }

    /// <summary>The HTTP status code.</summary>
    public int Status { get; }

    /// <summary>
    /// The stable machine-readable code, such as <c>event_not_found</c>, or
    /// <see langword="null"/> when the response carried none.
    /// </summary>
    public string? Code { get; }

    /// <summary>
    /// Extra context from the error body, or <see langword="null"/> when there
    /// was none. Its shape depends on <see cref="Code"/>.
    /// </summary>
    public JsonElement? Details { get; }

    /// <summary>
    /// The server-side trace id to quote in support requests, or
    /// <see langword="null"/>.
    /// </summary>
    public string? RequestId { get; }

    /// <summary>
    /// Reports whether this is a read for an event that does not exist.
    /// </summary>
    /// <remarks>
    /// One is readable as soon as identification answers, so this means a
    /// request id that was never issued rather than one that has not landed.
    /// </remarks>
    public bool IsEventNotFound => Status == 404 && Code == "event_not_found";

    /// <summary>
    /// The seconds the API asked you to wait before trying again, from
    /// <c>Retry-After</c> (an HTTP date becomes seconds from now), or null
    /// when it sent none.
    /// </summary>
    /// <remarks>
    /// The SDK never retries by itself; a 429 carries this so you can.
    /// </remarks>
    public int? RetryAfter { get; }

    private static string Describe(int status, string? code)
    {
        string prefix = string.Format(
            CultureInfo.InvariantCulture, "Fingerly API returned HTTP {0}", status);
        return string.IsNullOrEmpty(code) ? prefix : prefix + " (" + code + ")";
    }
}

/// <summary>
/// Thrown when an operation does not finish within its time limit.
/// </summary>
/// <remarks>
/// The deadline covers reading the response body, not only the headers. A
/// <see cref="System.Threading.CancellationToken"/> the caller cancelled
/// raises <see cref="OperationCanceledException"/> instead, so the two reasons
/// a call stopped early stay distinguishable.
/// </remarks>
public class FingerlyTimeoutException : FingerlyException
{
    /// <summary>Creates a timeout with a message.</summary>
    /// <param name="message">What timed out.</param>
    public FingerlyTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a timeout with a message and an underlying cause.</summary>
    /// <param name="message">What timed out.</param>
    /// <param name="innerException">The failure this one wraps.</param>
    public FingerlyTimeoutException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when the Fingerly API could not be reached at all.
/// </summary>
/// <remarks>
/// A DNS failure, a refused connection or a TLS error arrives here. The
/// originating exception is kept as the cause.
/// </remarks>
public class FingerlyConnectionException : FingerlyException
{
    /// <summary>Creates a connection failure with a message.</summary>
    /// <param name="message">What could not be reached.</param>
    public FingerlyConnectionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a connection failure with a message and a cause.</summary>
    /// <param name="message">What could not be reached.</param>
    /// <param name="innerException">The failure this one wraps.</param>
    public FingerlyConnectionException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
