using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Fingerly;

/// <summary>
/// Reports the requests your server served and remembers what the server
/// answered, so AI crawlers and fetchers that never run a page's JavaScript
/// are seen, and your code can ask what sent a request.
/// </summary>
/// <remarks>
/// <para>
/// Build one per process and reuse it. <see cref="Report"/> and
/// <see cref="Lookup"/> work in memory only and never wait on the network:
/// reports are batched and sent by a background timer that does not keep the
/// process alive. Under ASP.NET Core, <c>app.UseFingerlyAgentSightings()</c>
/// reports for you.
/// </para>
/// <para>
/// Memory is bounded: at most <see cref="AgentSightingsOptions.Capacity"/>
/// answers (4096 by default, about 1.5 MB) and 1000 pending or in-flight requests. Every
/// failure fails open: requests are served as usual and <see cref="Lookup"/>
/// answers <see cref="AgentAnswer.Unknown"/>.
/// </para>
/// <para>
/// Sent per request: the <c>User-Agent</c>, the address from
/// <see cref="AgentSightingsOptions.ClientIp"/>, the <c>Host</c>, the method,
/// the three Web Bot Auth headers when present, and a count. Never sent: the
/// path, the query string, cookies, <c>Authorization</c>, other headers, the
/// body or the response.
/// </para>
/// </remarks>
public sealed class AgentSightings : IDisposable
#if NET
    , IAsyncDisposable
#endif
{
    /// <summary>A report is sent once this many requests are pending.</summary>
    internal const int FlushItems = AgentsService.MaxItems;

    /// <summary>A report is sent once the pending requests are about this large.</summary>
    internal const int FlushBytes = 64 * 1024;

    /// <summary>A report is sent once the oldest pending request is this old.</summary>
    internal const long FlushAgeMs = 2000;

    /// <summary>The most requests held pending.</summary>
    internal const int BufferLimit = 1000;

    /// <summary>The largest count the API accepts for one item.</summary>
    internal const long MaxCount = 1_000_000;

    /// <summary>The deadline for one report.</summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private const int TickMs = 250;
    private const long AuthPauseMs = 60_000;
    private const long LogEveryMs = 60_000;
    private const long MaxBackoffMs = 60_000;
    private const long MaxTtlSeconds = 86_400;
    private const long MaxRetryAfterSeconds = 86_400;

    private static readonly string[] WellKnownPaths = ["/robots.txt", "/llms.txt", "/llms-full.txt"];
    private static readonly string[] DefaultContentTypes = ["text/html", "application/xhtml+xml"];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly FingerlyClient _client;
    private readonly bool _ownsClient;
    private readonly Func<ProxyRequest, string?> _clientIp;
    private readonly int _capacity;
    private readonly HashSet<string> _contentTypes;
    private readonly string[] _include;
    private readonly string[] _exclude;
    private readonly Action<string>? _log;
    private readonly Func<long> _now;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Timer? _timer;
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private readonly object _gate = new();

    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _recency = new();
    private string? _rulesVersion;

    private readonly LinkedList<Pending> _pending = new();
    private readonly Dictionary<string, LinkedListNode<Pending>> _folded = new(StringComparer.Ordinal);
    private long _pendingBytes;
    private int _inFlight;

    private long _pausedUntil;
    private int _backoffSteps;
    private readonly Dictionary<string, long> _lastLogged = new(StringComparer.Ordinal);
    private int _signalled;
    private long _dropped;
    private int _disposed;

    /// <summary>
    /// Builds the component over a client, validating every option first, and
    /// starts its background timer.
    /// </summary>
    /// <param name="client">
    /// The client to report with; its secret key authenticates the reports. It
    /// is not disposed with this instance.
    /// </param>
    /// <param name="options">The settings; <see cref="AgentSightingsOptions.ClientIp"/> is required.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">ClientIp is missing, or a content type or path prefix is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is below 1. One above <see cref="AgentSightingsOptions.MaxCapacity"/> is held to it.</exception>
    public AgentSightings(FingerlyClient client, AgentSightingsOptions options)
        : this(client, options, ownsClient: false, fallbackLog: null, now: null, utcNow: null, startTimer: true)
    {
    }

    /// <summary>
    /// The full constructor: the ASP.NET Core registration passes a client it
    /// owns and the application's logger, and tests pass their own clocks and
    /// drive the timer by hand.
    /// </summary>
    internal AgentSightings(
        FingerlyClient client, AgentSightingsOptions options, bool ownsClient, Action<string>? fallbackLog,
        Func<long>? now, Func<DateTimeOffset>? utcNow, bool startTimer)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        _clientIp = options.ClientIp ?? throw new ArgumentException("ClientIp is required", nameof(options));
        if (options.Capacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.Capacity, "Capacity must be at least 1");
        }
        // Above the hard maximum is held to it rather than refused.
        _capacity = Math.Min(options.Capacity, AgentSightingsOptions.MaxCapacity);
        _contentTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? type in options.ContentTypes ?? DefaultContentTypes)
        {
            if (string.IsNullOrWhiteSpace(type) || type!.IndexOf(';') >= 0)
            {
                throw new ArgumentException("A content type is a media type such as text/html", nameof(options));
            }
            _contentTypes.Add(type.Trim().ToLowerInvariant());
        }
        _include = Prefixes(options.IncludePathPrefixes, nameof(options));
        _exclude = Prefixes(options.ExcludePathPrefixes, nameof(options));
        _log = options.Log ?? fallbackLog;
        _ownsClient = ownsClient;
        _now = now ?? (() => Clock.ElapsedMilliseconds);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

        if (startTimer)
        {
            // The timer must not carry the context of whatever request built
            // this instance into every later report, nor keep it alive.
            using (ExecutionContext.SuppressFlow())
            {
                _timer = new Timer(OnTick, new WeakReference<AgentSightings>(this), TickMs, TickMs);
            }
        }
    }

    /// <summary>
    /// How many requests were not reported because 1000 were already pending
    /// or in flight, since this instance was built.
    /// </summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Reports whether a response is one this instance reports: never under an
    /// excluded prefix; always for <c>/robots.txt</c>, <c>/llms.txt</c> and
    /// <c>/llms-full.txt</c> and under an included prefix; otherwise when its
    /// content type is one of <see cref="AgentSightingsOptions.ContentTypes"/>.
    /// </summary>
    /// <param name="path">The application-relative path, without the query string.</param>
    /// <param name="responseContentType">The response's <c>Content-Type</c>, or null.</param>
    /// <returns>True when the request is reported.</returns>
    public bool InScope(string? path, string? responseContentType)
    {
        path ??= string.Empty;
        foreach (string prefix in _exclude)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }
        foreach (string known in WellKnownPaths)
        {
            if (string.Equals(path, known, StringComparison.Ordinal))
            {
                return true;
            }
        }
        foreach (string prefix in _include)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        if (string.IsNullOrEmpty(responseContentType))
        {
            return false;
        }
        int parameters = responseContentType!.IndexOf(';');
        string media = (parameters >= 0 ? responseContentType.Substring(0, parameters) : responseContentType).Trim();
        return _contentTypes.Contains(media.ToLowerInvariant());
    }

    /// <summary>
    /// Counts one served request for the next report, when its response is in
    /// scope (see <see cref="InScope"/>). Call it once the response exists.
    /// </summary>
    /// <remarks>
    /// In memory only; it never waits on the network and never throws. A
    /// request with no usable address, or a <c>User-Agent</c> or method over
    /// 4096 UTF-8 bytes, is not reported; a longer <c>Host</c> or signature
    /// header is left out of the report.
    /// </remarks>
    /// <param name="request">The request, with its headers and <see cref="ProxyRequest.RemoteAddress"/>.</param>
    /// <param name="responseContentType">The response's <c>Content-Type</c>, or null.</param>
    public void Report(ProxyRequest request, string? responseContentType)
    {
        try
        {
            if (request is null || Volatile.Read(ref _disposed) != 0
                || !InScope(request.Path, responseContentType))
            {
                return;
            }
            Sighting? sighting = Prepare(request);
            if (sighting is not null)
            {
                Record(sighting);
            }
        }
#pragma warning disable CA1031 // Reporting must never affect the response being served.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// What sent a request, as the server answered for the same user agent,
    /// address and <c>Signature-Agent</c>, or <see cref="AgentAnswer.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// In memory only; it never waits on the network and never throws. Treat
    /// <see cref="AgentAnswer.Unknown"/> as "serve as usual": it is the
    /// answer for a request never reported, one whose answer expired, a signed
    /// request (checked every time), and every request while reporting fails.
    /// </remarks>
    /// <param name="request">The request, with its headers and <see cref="ProxyRequest.RemoteAddress"/>.</param>
    /// <returns>The remembered answer, or <see cref="AgentAnswer.Unknown"/>.</returns>
    public AgentAnswer Lookup(ProxyRequest request)
    {
        try
        {
            return request is null ? AgentAnswer.Unknown : LookupPrepared(Prepare(request));
        }
#pragma warning disable CA1031 // Fail open: a lookup never throws.
        catch (Exception)
#pragma warning restore CA1031
        {
            return AgentAnswer.Unknown;
        }
    }

    /// <summary>
    /// Sends the pending requests, in reports of up to 200, until none is
    /// left, reporting pauses after a refusal or failure, or a report is
    /// dropped. Waits for a report already in flight first, for at most six
    /// seconds; when that one is still running, returns without sending.
    /// </summary>
    /// <remarks>
    /// The background timer does this by itself. Call it where a process may
    /// end without warning, such as before a serverless invocation returns.
    /// Failures are handled as the timer handles them, and never thrown. The
    /// one exception is your own cancellation: when
    /// <paramref name="cancellationToken"/> is cancelled, it throws
    /// <see cref="OperationCanceledException"/>, as any .NET call does.
    /// </remarks>
    /// <param name="cancellationToken">Stops waiting; a report already sent is not retried.</param>
    /// <returns>A task that completes when the pending requests were sent or kept.</returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled. Nothing else is thrown.
    /// </exception>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!await _flushing.WaitAsync(InFlightWait, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            while (await SendBatchAsync(cancellationToken).ConfigureAwait(false) == Outcome.Sent)
            {
            }
        }
        finally
        {
            _flushing.Release();
        }
    }

    /// <summary>
    /// Stops the timer and makes one last report, within five seconds, unless
    /// reporting is paused. Later requests are not reported;
    /// <see cref="Lookup"/> keeps answering from what is remembered.
    /// </summary>
    public void Dispose()
    {
        if (!BeginDispose())
        {
            return;
        }
        try
        {
            Task.Run(FinalFlushAsync).Wait(ReportDeadline + TimeSpan.FromSeconds(1));
        }
#pragma warning disable CA1031 // Shutting down must not throw over a report.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        EndDispose();
    }

#if NET
    /// <summary>
    /// Stops the timer and makes one last report, within five seconds, unless
    /// reporting is paused. Later requests are not reported;
    /// <see cref="Lookup"/> keeps answering from what is remembered.
    /// </summary>
    /// <returns>A task that completes once the last report finished.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!BeginDispose())
        {
            return;
        }
        await FinalFlushAsync().ConfigureAwait(false);
        EndDispose();
    }
#endif

    /// <summary>Names the capacity; nothing remembered is described.</summary>
    /// <returns>A description safe to log.</returns>
    public override string ToString() => "AgentSightings { Capacity = " + _capacity + " }";

    /// <summary>The deadline for one report, and for the last one at shutdown; tests shorten it.</summary>
    internal TimeSpan ReportDeadline { get; set; } = Deadline;

    /// <summary>
    /// How long <see cref="FlushAsync"/> waits for a report already in flight:
    /// its deadline and a second of margin. Tests shorten it.
    /// </summary>
    internal TimeSpan InFlightWait { get; set; } = Deadline + TimeSpan.FromSeconds(1);

    /// <summary>Requests pending, for tests.</summary>
    internal int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Answers remembered, expired ones included, for tests.</summary>
    internal int CachedCount
    {
        get
        {
            lock (_gate)
            {
                return _cache.Count;
            }
        }
    }

    /// <summary>The pending count for a key, or 0, for tests.</summary>
    internal long PendingCountFor(string key)
    {
        lock (_gate)
        {
            return _folded.TryGetValue(key, out LinkedListNode<Pending>? node) ? node.Value.Count : 0;
        }
    }

    /// <summary>Milliseconds until reporting resumes, or 0, for tests.</summary>
    internal long PausedForMs
    {
        get
        {
            lock (_gate)
            {
                return Math.Max(0, _pausedUntil - _now());
            }
        }
    }

    /// <summary>Prepares a request for the cache and the report, or null when it is not reported.</summary>
    internal Sighting? Prepare(ProxyRequest request)
    {
        string? address;
        try
        {
            address = _clientIp(request);
        }
#pragma warning disable CA1031 // A throwing callback is the same misconfiguration as a null one.
        catch (Exception)
#pragma warning restore CA1031
        {
            address = null;
        }
        if (Sighting.CanonicalIp(address) is null)
        {
            LogThrottled("client-ip", FingerlyProxy.NoClientIp + "; those requests are not reported");
            return null;
        }
        return Sighting.Prepare(
            request.Method, request.Header("User-Agent"), address, request.Header("Host"),
            request.Header("Signature"), request.Header("Signature-Input"), request.Header("Signature-Agent"));
    }

    /// <summary>The remembered answer for a prepared request.</summary>
    internal AgentAnswer LookupPrepared(Sighting? sighting)
    {
        if (sighting is null)
        {
            return AgentAnswer.Unknown;
        }
        lock (_gate)
        {
            if (!_cache.TryGetValue(sighting.Key, out LinkedListNode<CacheEntry>? node))
            {
                return AgentAnswer.Unknown;
            }
            if (node.Value.ExpiresAt <= _now())
            {
                _cache.Remove(sighting.Key);
                _recency.Remove(node);
                return AgentAnswer.Unknown;
            }
            _recency.Remove(node);
            _recency.AddFirst(node);
            return node.Value.Answer;
        }
    }

    /// <summary>
    /// Counts a prepared request: into the pending entry for its key, or a new
    /// one. A signed request is always its own entry.
    /// </summary>
    internal void Record(Sighting sighting)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        bool due;
        lock (_gate)
        {
            long now = _now();
            if (!sighting.Signed && _folded.TryGetValue(sighting.Key, out LinkedListNode<Pending>? existing))
            {
                existing.Value.Count = Math.Min(MaxCount, existing.Value.Count + 1);
            }
            else if (_pending.Count + _inFlight >= BufferLimit)
            {
                // The requests of a report in flight count too, so a kept
                // report always fits back.
                Interlocked.Increment(ref _dropped);
                return;
            }
            else
            {
                LinkedListNode<Pending> node = _pending.AddLast(new Pending(sighting, 1, now));
                if (!sighting.Signed)
                {
                    _folded[sighting.Key] = node;
                }
                _pendingBytes += sighting.EstimatedBytes;
            }
            due = (_pending.Count >= FlushItems || _pendingBytes >= FlushBytes) && now >= _pausedUntil;
        }
        if (due && Interlocked.Exchange(ref _signalled, 1) == 0)
        {
            // Sent from the thread pool, never from the request being served,
            // and without that request's context.
            ThreadPool.UnsafeQueueUserWorkItem(
                static state => _ = ((AgentSightings)state!).TickAsync(), this);
        }
    }

    /// <summary>
    /// One turn of the background flusher: sends reports while one is due and
    /// no other is in flight.
    /// </summary>
    internal async Task TickAsync()
    {
        Interlocked.Exchange(ref _signalled, 0);
        if (Volatile.Read(ref _disposed) != 0 || !_flushing.Wait(0))
        {
            return;
        }
        try
        {
            while (Due())
            {
                if (await SendBatchAsync(CancellationToken.None).ConfigureAwait(false) != Outcome.Sent)
                {
                    return;
                }
            }
        }
#pragma warning disable CA1031 // The timer thread must survive anything a report does.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            _flushing.Release();
        }
    }

    private static void OnTick(object? state)
    {
        var reference = (WeakReference<AgentSightings>)state!;
        if (reference.TryGetTarget(out AgentSightings? sightings))
        {
            _ = sightings.TickAsync();
        }
    }

    private bool Due()
    {
        lock (_gate)
        {
            LinkedListNode<Pending>? oldest = _pending.First;
            long now = _now();
            return oldest is not null && now >= _pausedUntil
                && (_pending.Count >= FlushItems || _pendingBytes >= FlushBytes
                    || now - oldest.Value.AddedAt >= FlushAgeMs);
        }
    }

    /// <summary>What became of one report.</summary>
    private enum Outcome
    {
        /// <summary>Nothing was sent: nothing pending, or reporting is paused.</summary>
        Idle,

        /// <summary>The server answered and the answers are remembered.</summary>
        Sent,

        /// <summary>The report is back in the buffer, and reporting paused.</summary>
        Kept,

        /// <summary>The report was dropped after a failure.</summary>
        Dropped,
    }

    /// <summary>
    /// Sends the oldest pending requests, up to 200 and 64 KiB, and applies
    /// the answer. The caller holds <see cref="_flushing"/>.
    /// </summary>
    private async Task<Outcome> SendBatchAsync(CancellationToken cancellationToken)
    {
        List<Pending> batch = Take();
        if (batch.Count == 0)
        {
            return Outcome.Idle;
        }
        try
        {
            return await SendAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight = 0;
            }
        }
    }

    /// <summary>Moves the oldest pending requests in flight, unless paused.</summary>
    private List<Pending> Take()
    {
        var batch = new List<Pending>();
        lock (_gate)
        {
            if (_now() < _pausedUntil)
            {
                return batch;
            }
            long bytes = 0;
            for (LinkedListNode<Pending>? node = _pending.First; node is not null && batch.Count < FlushItems;)
            {
                int size = node.Value.Sighting.EstimatedBytes;
                if (batch.Count > 0 && bytes + size > FlushBytes)
                {
                    break;
                }
                LinkedListNode<Pending>? next = node.Next;
                RemovePending(node);
                batch.Add(node.Value);
                bytes += size;
                node = next;
            }
            _inFlight = batch.Count;
        }
        return batch;
    }

    private async Task<Outcome> SendAsync(List<Pending> batch, CancellationToken cancellationToken)
    {
        var items = new List<AgentSighting>(batch.Count);
        foreach (Pending pending in batch)
        {
            items.Add(pending.Sighting.ToItem(pending.Count));
        }
        var body = new TrackedJsonContent(AgentsService.Encode(items));

        AgentSightingsResult result;
        try
        {
            result = await _client.Agents.SendAsync(body, ReportDeadline, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!body.Started)
            {
                Keep(batch, backoff: false);
            }
            throw;
        }
        catch (FingerlyApiException refused)
        {
            return Refused(refused, batch);
        }
        catch (FingerlyTimeoutException)
        {
            if (!body.Started)
            {
                // The connection was still being opened: nothing left.
                Keep(batch, backoff: true);
                return Outcome.Kept;
            }
            // The server may have counted it; a retry would count it twice.
            LogThrottled("timeout", "a report got no answer within 5 s and was dropped");
            return Outcome.Dropped;
        }
        catch (FingerlyConnectionException)
        {
            // HttpClient raises the same error for a connection that failed
            // before the request left and for one closed or reset after it,
            // or for an answer cut short. The body tells them apart: once it
            // was written and flushed, the server may have counted it.
            if (body.Written)
            {
                LogThrottled("lost", "the connection closed after a report was sent; it was dropped");
                return Outcome.Dropped;
            }
            Keep(batch, backoff: true);
            return Outcome.Kept;
        }
#pragma warning disable CA1031 // An unreadable answer is dropped, as a refused one is.
        catch (Exception)
#pragma warning restore CA1031
        {
            LogThrottled("answer", "the API answered a report with a body this SDK cannot read; it was dropped");
            return Outcome.Dropped;
        }

        if (result.Items.Count != batch.Count)
        {
            LogThrottled("answer", "the API answered a report with a body this SDK cannot read; it was dropped");
            return Outcome.Dropped;
        }
        Store(result, batch);
        return Outcome.Sent;
    }

    /// <summary>Applies an error answer per the failure table.</summary>
    private Outcome Refused(FingerlyApiException refused, List<Pending> batch)
    {
        int status = refused.Status;
        if (status == 429)
        {
            // Held to one day: the daily allowance renews within one, and a
            // larger value (a box in front of the API) would stop reporting
            // for the life of the process.
            long seconds = Math.Min(MaxRetryAfterSeconds, refused.RetryAfter
                ?? (refused.RateLimitScope == RateLimitScopes.Daily ? SecondsToNextUtcDay() : 60));
            lock (_gate)
            {
                MergeBack(batch);
                _pausedUntil = _now() + (Math.Max(1, seconds) * 1000);
            }
            LogThrottled("429", "reports are rate limited; requests are served unchecked until the limit resets");
            return Outcome.Kept;
        }
        if (status == 503)
        {
            Keep(batch, backoff: true);
            return Outcome.Kept;
        }
        if (status == 401 || status == 403)
        {
            lock (_gate)
            {
                _pausedUntil = _now() + AuthPauseMs;
            }
            LogThrottled("auth", "the API refused the secret key (HTTP " + status + "); the key is wrong or revoked");
            return Outcome.Dropped;
        }
        // 422 (no item in the batch was usable) is an SDK bug, and so is any
        // other answer the table does not name (500, 502, 504, 413, other
        // 4xx): the server may have acted on part of it, so it is never
        // retried.
        LogThrottled("refused", "the API refused a report (HTTP " + status + "); it was dropped");
        return Outcome.Dropped;
    }

    /// <summary>Remembers every answer with a positive ttl, after a rules change clears the rest.</summary>
    private void Store(AgentSightingsResult result, List<Pending> batch)
    {
        lock (_gate)
        {
            _backoffSteps = 0;
            if (_rulesVersion is not null && !string.Equals(_rulesVersion, result.RulesVersion, StringComparison.Ordinal))
            {
                _cache.Clear();
                _recency.Clear();
            }
            _rulesVersion = result.RulesVersion;
            long now = _now();
            for (int i = 0; i < batch.Count; i++)
            {
                AgentAnswer? answer = result.Items[i];
                if (answer is null || answer.Ttl <= 0)
                {
                    continue;
                }
                var entry = new CacheEntry(
                    batch[i].Sighting.Key, answer, now + (Math.Min(answer.Ttl, MaxTtlSeconds) * 1000));
                if (_cache.TryGetValue(entry.Key, out LinkedListNode<CacheEntry>? existing))
                {
                    _recency.Remove(existing);
                }
                _cache[entry.Key] = _recency.AddFirst(entry);
                while (_cache.Count > _capacity)
                {
                    LinkedListNode<CacheEntry> last = _recency.Last!;
                    _recency.RemoveLast();
                    _cache.Remove(last.Value.Key);
                }
            }
        }
    }

    /// <summary>Puts a batch back, optionally backing off exponentially from 1 s to 60 s.</summary>
    private void Keep(List<Pending> batch, bool backoff)
    {
        lock (_gate)
        {
            MergeBack(batch);
            if (backoff)
            {
                long delay = Math.Min(MaxBackoffMs, 1000L << Math.Min(_backoffSteps, 16));
                _backoffSteps++;
                _pausedUntil = _now() + delay;
            }
        }
    }

    /// <summary>
    /// Returns a batch to the front of the buffer, in order, adding newer
    /// counts for the same key. Called under <see cref="_gate"/>.
    /// </summary>
    private void MergeBack(List<Pending> batch)
    {
        // The batch counted against the buffer limit while in flight, so it
        // always fits back; each item keeps its first-seen time.
        _inFlight = 0;
        for (int i = batch.Count - 1; i >= 0; i--)
        {
            Pending kept = batch[i];
            if (!kept.Sighting.Signed && _folded.TryGetValue(kept.Sighting.Key, out LinkedListNode<Pending>? newer))
            {
                kept.Count = Math.Min(MaxCount, kept.Count + newer.Value.Count);
                RemovePending(newer);
            }
            LinkedListNode<Pending> node = _pending.AddFirst(kept);
            if (!kept.Sighting.Signed)
            {
                _folded[kept.Sighting.Key] = node;
            }
            _pendingBytes += kept.Sighting.EstimatedBytes;
        }
    }

    /// <summary>Takes a node out of the buffer. Called under <see cref="_gate"/>.</summary>
    private void RemovePending(LinkedListNode<Pending> node)
    {
        _pending.Remove(node);
        _pendingBytes -= node.Value.Sighting.EstimatedBytes;
        if (!node.Value.Sighting.Signed
            && _folded.TryGetValue(node.Value.Sighting.Key, out LinkedListNode<Pending>? folded)
            && ReferenceEquals(folded, node))
        {
            _folded.Remove(node.Value.Sighting.Key);
        }
    }

    private long SecondsToNextUtcDay()
    {
        DateTimeOffset now = _utcNow().ToUniversalTime();
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1);
        return (long)Math.Ceiling((next - now).TotalSeconds);
    }

    private bool BeginDispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return false;
        }
        _timer?.Dispose();
        return true;
    }

    private void EndDispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    /// <summary>The last report, all of it within one deadline.</summary>
    private async Task FinalFlushAsync()
    {
        using var deadline = new CancellationTokenSource(ReportDeadline);
        try
        {
            await FlushAsync(deadline.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Shutting down must not throw over a report.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void LogThrottled(string cause, string line)
    {
        lock (_gate)
        {
            long now = _now();
            if (_lastLogged.TryGetValue(cause, out long last) && now - last < LogEveryMs)
            {
                return;
            }
            _lastLogged[cause] = now;
        }
        Log(line);
    }

    private void Log(string line)
    {
        try
        {
            _log?.Invoke("Fingerly agent sightings: " + line);
        }
#pragma warning disable CA1031 // A logging hook that throws is ignored.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static string[] Prefixes(IReadOnlyList<string>? prefixes, string paramName)
    {
        if (prefixes is null)
        {
            return [];
        }
        var result = new string[prefixes.Count];
        for (int i = 0; i < prefixes.Count; i++)
        {
            string? prefix = prefixes[i];
            if (string.IsNullOrEmpty(prefix) || prefix[0] != '/')
            {
                throw new ArgumentException("A path prefix starts with /", paramName);
            }
            result[i] = prefix;
        }
        return result;
    }

    private sealed class CacheEntry
    {
        internal CacheEntry(string key, AgentAnswer answer, long expiresAt)
        {
            Key = key;
            Answer = answer;
            ExpiresAt = expiresAt;
        }

        internal string Key { get; }

        internal AgentAnswer Answer { get; }

        internal long ExpiresAt { get; }
    }

    internal sealed class Pending
    {
        internal Pending(Sighting sighting, long count, long addedAt)
        {
            Sighting = sighting;
            Count = count;
            AddedAt = addedAt;
        }

        internal Sighting Sighting { get; }

        internal long Count { get; set; }

        internal long AddedAt { get; }
    }
}
