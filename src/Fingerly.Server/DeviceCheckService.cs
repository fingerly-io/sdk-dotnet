using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fingerly;

/// <summary>
/// Spends the Apple DeviceCheck token held for an iOS event, once, to read or
/// to write the device's two bits. Reached as <c>fingerly.DeviceCheck</c>.
/// </summary>
/// <remarks>
/// <para>
/// A token is held for one hour after an identification whose App Attest
/// evidence covered it, and pays for one Apple call: a read or a write, never
/// both. To read and then mark a device, read on one identification and write
/// on the next.
/// </para>
/// <para>
/// A call that answers <c>503 device_check_unavailable</c>, or that fails in
/// transport or times out, is retried with the identical request: Apple lets
/// a token be retried only for the same request, and the API answers
/// <c>409 device_check_spent</c> to any other. No other answer is retried.
/// </para>
/// </remarks>
public sealed class DeviceCheckService
{
    /// <summary>Retries after the first attempt.</summary>
    internal const int MaxRetries = 2;

    private readonly FingerlyClient _client;

    internal DeviceCheckService(FingerlyClient client) => _client = client;

    /// <summary>The pause before retry <paramref name="attempt"/> (1-based).</summary>
    internal static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(200 * attempt);

    /// <summary>
    /// Reads the device's two bits from Apple, spending the event's token.
    /// </summary>
    /// <param name="requestId">The <c>request_id</c> of an iOS identification made within the last hour.</param>
    /// <param name="cancellationToken">Cancels the call, retries included.</param>
    /// <returns>The bits Apple holds for the device.</returns>
    /// <exception cref="ArgumentException"><paramref name="requestId"/> is empty, "." or "..".</exception>
    /// <exception cref="FingerlyApiException">
    /// Any non-2xx response; branch on <see cref="FingerlyApiException.Code"/>
    /// against <see cref="DeviceCheckErrors"/>.
    /// </exception>
    /// <exception cref="FingerlyTimeoutException">Every attempt exceeded the client timeout.</exception>
    /// <exception cref="FingerlyConnectionException">Every attempt failed to reach the API.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<DeviceCheckBits> QueryAsync(string requestId, CancellationToken cancellationToken = default)
    {
        string path = PathFor(requestId) + "/query";
        byte[] body = await SpendAsync(HttpMethod.Post, path, null, cancellationToken).ConfigureAwait(false);
        return Json.Read(body, FingerlyJsonContext.Default.DeviceCheckBits);
    }

    /// <summary>
    /// Sets bit0, bit1 or both with Apple, spending the event's token. A bit
    /// left null keeps its value.
    /// </summary>
    /// <param name="requestId">The <c>request_id</c> of an iOS identification made within the last hour.</param>
    /// <param name="bits">The bits to set; at least one must be non-null.</param>
    /// <param name="cancellationToken">Cancels the call, retries included.</param>
    /// <returns>The bits Apple accepted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bits"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="requestId"/> is empty, "." or "..", or
    /// <paramref name="bits"/> sets neither bit. Nothing is sent.
    /// </exception>
    /// <exception cref="FingerlyApiException">
    /// Any non-2xx response; branch on <see cref="FingerlyApiException.Code"/>
    /// against <see cref="DeviceCheckErrors"/>.
    /// </exception>
    /// <exception cref="FingerlyTimeoutException">Every attempt exceeded the client timeout.</exception>
    /// <exception cref="FingerlyConnectionException">Every attempt failed to reach the API.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<DeviceCheckWriteResult> WriteAsync(
        string requestId, DeviceCheckWrite bits, CancellationToken cancellationToken = default)
    {
        if (bits is null)
        {
            throw new ArgumentNullException(nameof(bits));
        }
        string path = PathFor(requestId);
        if (bits.Bit0 is null && bits.Bit1 is null)
        {
            throw new ArgumentException("Set Bit0, Bit1 or both", nameof(bits));
        }
        // Serialized once, so every retry sends the same bytes.
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(bits, FingerlyJsonContext.Default.DeviceCheckWrite);
        byte[] body = await SpendAsync(HttpMethod.Put, path, json, cancellationToken).ConfigureAwait(false);
        return Json.Read(body, FingerlyJsonContext.Default.DeviceCheckWriteResult);
    }

    private static string PathFor(string requestId)
    {
        if (string.IsNullOrEmpty(requestId) || requestId == "." || requestId == "..")
        {
            throw new ArgumentException("requestId is required", nameof(requestId));
        }
        return "events/" + Uri.EscapeDataString(requestId) + "/device-check";
    }

    private async Task<byte[]> SpendAsync(
        HttpMethod method, string path, byte[]? json, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await _client.SendAsync(method, path, json, cancellationToken).ConfigureAwait(false);
            }
            catch (FingerlyException error) when (attempt < MaxRetries && Retryable(error))
            {
                await Task.Delay(RetryDelay(attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool Retryable(FingerlyException error) =>
        error switch
        {
            FingerlyApiException api => api.Status == 503,
            FingerlyTimeoutException => true,
            FingerlyConnectionException => true,
            _ => false,
        };
}
