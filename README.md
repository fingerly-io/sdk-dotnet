<p align="center">
  <a href="https://fingerly.io">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/fingerly-io/.github/main/assets/logo-dark.svg">
      <img src="https://raw.githubusercontent.com/fingerly-io/.github/main/assets/logo-light.svg" alt="Fingerly" height="44">
    </picture>
  </a>
</p>

<p align="center"><b>Fingerly for .NET</b><br>Read device intelligence events, verify webhooks and serve the browser SDK from your own domain.</p>

<p align="center">
  <a href="https://www.nuget.org/packages/Fingerly.Server"><img src="https://img.shields.io/nuget/v/Fingerly.Server?style=flat-square&label=nuget" alt="NuGet version"></a>
  <a href="https://github.com/fingerly-io/sdk-dotnet/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/fingerly-io/sdk-dotnet/ci.yml?style=flat-square&label=ci" alt="CI status"></a>
  <a href="https://github.com/fingerly-io/sdk-dotnet/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-Apache--2.0-blue?style=flat-square" alt="License: Apache-2.0"></a>
  <img src="https://img.shields.io/badge/.NET-8.0%20%7C%20Standard%202.0-512BD4?style=flat-square" alt=".NET 8.0 and .NET Standard 2.0">
  <a href="https://docs.fingerly.io/docs/sdks/dotnet"><img src="https://img.shields.io/badge/docs-docs.fingerly.io-111827?style=flat-square" alt="Documentation"></a>
</p>

<p align="center"><img src="https://raw.githubusercontent.com/fingerly-io/.github/main/assets/banner.webp" alt="Fingerly" width="100%"></p>

## What is this

`Fingerly.Server` is the server-side SDK for .NET. It reads Fingerly events with
a secret key, verifies webhook signatures and relays the browser SDK's traffic
through your own domain with a same-origin proxy. It is for backends that
protect signups, logins and payments from fraud and abuse.
[Fingerly](https://fingerly.io) identifies the device behind each session and
scores how suspicious it is.

## Getting started

1. **Prerequisites.** A Fingerly account and a secret key (`fly_sk_...`). See
   [API keys and environments](https://docs.fingerly.io/docs/api-keys).
2. **Install.**

   ```sh
   dotnet add package Fingerly.Server --version 0.4.1
   dotnet add package Fingerly.Server.AspNetCore --version 0.4.1   # for the proxy and DI
   ```

3. **Read an event.**

   ```csharp
   using Fingerly;

   var builder = WebApplication.CreateBuilder(args);
   builder.Services.AddFingerly(options =>
   {
       options.SecretKey = builder.Configuration["Fingerly:SecretKey"]!;
   });
   var app = builder.Build();

   app.MapGet("/orders/{requestId}/check", async (string requestId, FingerlyClient fingerly, CancellationToken ct) =>
   {
       Event ev = await fingerly.Events.GetAsync(requestId, ct);
       if (string.IsNullOrEmpty(ev.VisitorId) || !ev.IsScored) return Results.Ok("review");
       return Results.Ok(ev.SuspectLevel);
   });

   app.Run();
   ```

4. **Verify a webhook.**

   ```csharp
   string secret = builder.Configuration["Fingerly:WebhookSecret"]!;

   app.MapPost("/webhooks/fingerly", async (HttpRequest request, CancellationToken ct) =>
   {
       using var body = new MemoryStream();
       await request.Body.CopyToAsync(body, ct);
       byte[] payload = body.ToArray();

       if (!Webhooks.Verify(secret, payload,
               request.Headers["x-fingerly-timestamp"],
               request.Headers["x-fingerly-signature"]))
       {
           return Results.BadRequest();
       }

       WebhookEvent ev = Webhooks.Parse(payload);
       // Queue ev.Id and payload for processing, deduplicating on ev.Id.
       return Results.Ok();
   });
   ```

Full guide: [docs.fingerly.io/docs/sdks/dotnet](https://docs.fingerly.io/docs/sdks/dotnet).

## Choose what you need

| I want to | Use | Read |
| --- | --- | --- |
| Read an event after the browser identified a visitor | `FingerlyClient` and `Events.GetAsync` / `ListAsync` | [Read events](#read-events) |
| Verify and consume an event before a backend action | `Events.VerifyAsync` / `ConsumeAsync` | [Verify a backend action](#verify-a-backend-action) |
| Read or write the DeviceCheck bits of an iOS device | `DeviceCheck.QueryAsync` / `WriteAsync` | [DeviceCheck bits](#devicecheck-bits) |
| Check a webhook delivery is genuine | `Webhooks.Verify` and `Webhooks.Parse` | [Verify webhooks](#verify-webhooks) |
| Keep the browser SDK first-party, away from content blockers | `AddFingerlyProxy` with `MapFingerlyProxy` or `UseFingerlyProxy` | [First-party proxy](#first-party-proxy-same-origin) |
| See AI crawlers that never run JavaScript, and ask what sent a request | `AddFingerlyAgentSightings` with `UseFingerlyAgentSightings` and `Lookup` | [AI crawlers on your server](#ai-crawlers-on-your-server) |

On this page: [Packages](#packages) · [Read events](#read-events) · [DeviceCheck bits](#devicecheck-bits) · [Verify webhooks](#verify-webhooks) · [First-party proxy](#first-party-proxy-same-origin) · [AI crawlers on your server](#ai-crawlers-on-your-server) · [Deadlines, limits and connections](#deadlines-limits-and-connections) · [Requirements](#requirements) · [Development](#development-and-release-preparation) · [Documentation](#documentation) · [Help](#help) · [Licence](#licence)

## Packages

| Package | Contains | For |
| --- | --- | --- |
| `Fingerly.Server` | client, webhooks, proxy core | everything |
| `Fingerly.Server.AspNetCore` | middleware, endpoint routing, DI | the proxy and agent sightings |

`Fingerly.Server` targets `net8.0` and `netstandard2.0`. On `net8.0` it has no
dependencies at all, because `System.Text.Json` is part of the shared
framework; on `netstandard2.0` that one package is the only dependency, which
is what keeps the client and webhook verification reachable from .NET
Framework 4.6.2 and newer. `Fingerly.Server.AspNetCore` targets `net8.0` and
takes ASP.NET Core as a framework reference, so nothing here collides with
what your application server already has.

Both packages are marked trimmable and AOT-safe. Every type is bound through
the `System.Text.Json` source generator rather than reflection, so a trimmed or
natively compiled application gets no warnings from this SDK and no binding
that the trimmer can quietly break.

Everything is asynchronous. There is no synchronous counterpart to any call, on
purpose. Blocking a request thread on I/O is what exhausts a thread pool under
load, and the deadlock that `.Result` can cause is not one an SDK could warn
about at the call site. Never call `.Result` or `.Wait()` on these methods.

## Read events

Event reads require `fly_sk_{production|staging|development}_…` keys. Requests
go to `https://api.fingerly.io`; `Endpoint` overrides that for a local
API and takes a bare origin only: `https://host` or `https://host:port`, with
no credentials, path, query, fragment, space or control character. Plain HTTP
is refused, except for `http://127.0.0.1`, `http://localhost` or
`http://[::1]` (any port) when `AllowInsecureLoopback` is set, which is for a
local test harness. The error never repeats the value. Public, proxy,
management, malformed and unsupported keys are rejected before network access,
by the constructor rather than by the first call. Requests do not follow
redirects. `GetAsync` and `ListAsync` each perform one request and never
retry. An empty, `.` or `..` request id is an `ArgumentException`.

```csharp
EventPage page = await fingerly.Events.ListAsync(
    new EventListOptions { Level = SuspectLevels.High, Limit = 50 }, cancellationToken);
```

`Event.ProjectId` is the UUID of the Fingerly project the event belongs to, on
`GetAsync` and on every `ListAsync` row; it is null when an older server does
not send it.

`Event.Ai` is set only when `Event.VisitorKind` is `VisitorKinds.AiBot`, and
null otherwise: an `AiAgent` with the agent's `Name`, `Provider`, `Purpose`,
`Runtime`, `Declared` and `Verification`. The agent is named by a Web Bot Auth
signature when the request carried one, otherwise by its user agent.
`Verification` says how far that was checked:
`AiVerificationMethods.Signature` (the operator's published key signed the
request) or `AiVerificationMethods.IpList` (the address is inside the
operator's published ranges), with result `AiVerificationResults.Pass`,
`AiVerificationResults.Fail` (the name was borrowed), or
`AiVerificationResults.NotAttempted` with method `AiVerificationMethods.None`
(the operator publishes nothing to check against, or it could not be checked;
the name is then only the client's claim). Only clients that run the Fingerly
SDK produce events, so crawlers that do not run JavaScript never appear. `EventListOptions`
filters on `Kind`, `AiProvider` (exact and case-sensitive, such as `OpenAI`)
and `AiPurpose`.

```csharp
EventPage fetches = await fingerly.Events.ListAsync(
    new EventListOptions { Kind = VisitorKinds.AiBot, AiPurpose = AiPurposes.UserFetch },
    cancellationToken);
```

`Raw` keeps the response body exactly as it arrived, so a field the API adds
later is reachable without an SDK upgrade.

### Unscored events

`Event.SuspectScore` is `int?`, null when the request was not scored. Never
read null as zero: a visitor about whom nothing fired scores 0, while a
submission that was never enriched has no score at all. `IsScored` and
`TryGetScore` state the distinction at the call site.

```csharp
if (!ev.IsScored)
{
    // Decide on something else; do not treat it as clean.
}
```

### Read an event you just identified

An event is usually readable as soon as identification answers. If it is
still being recorded, the read throws a `FingerlyApiException` whose
`IsPending` is true and whose `RetryAfter` says when to read again:

```csharp
Event ev;
try
{
    ev = await fingerly.Events.GetAsync(requestId, ct);
}
catch (FingerlyApiException e) when (e.IsPending)
{
    // Not recorded yet, so nothing is decided: read again after
    // e.RetryAfter seconds (2 when null), or verify asynchronously.
    return "retry";
}
```

Retry a pending read a bounded number of times, for example from a background
job. `IsEventNotFound` means a request id that was never issued, or one past
retention, and is never pending.

The archived document arrives moments after the event itself, so check
`HasDocument` rather than assuming it is there, and inspect `Document`
(`JsonElement?`, containing allow-listed `risk`, `attestation`, `ip_network` and
`velocity` fields with no raw signals, user agent or address). When a submission
is unidentifiable (`identifiable == false`, such as insufficient entropy, a
genuine embedded WebView, or an unmatched high-risk Web payload), `VisitorId` is
`""`; there are no `deferred_token`, `deferred_signals` or `supplements` fields
in `v: 3`.

### Named values

The string properties with a fixed set of values have a static class of
`const string` values each, holding the exact strings the API sends. The
properties stay `string`, so a value the server adds after this SDK was
released still deserializes: compare with `==` or use the constants as
`switch` labels, and keep a default branch. The class names are plural so
they never collide with the properties they describe (or with
`System.Environment`).

| Constants | Values of |
| --- | --- |
| `Signals` | `EventTrigger.Signal` (signal groups, such as `Signals.Vpn`) and the archived document's `risk.triggers[].signal` (detectors, such as `Signals.VpnPublic`) |
| `SuspectLevels` | `Event.SuspectLevel` and `EventListOptions.Level` |
| `Confidences` | `EventTrigger.Confidence` |
| `EventStates`, `Environments`, `Platforms` | `Event.State`, `Event.Environment`, `Event.Platform` |
| `AnonymityNetworks`, `DeviceKinds`, `VisitorKinds` | `Event.AnonymityNetwork`, `Event.DeviceKind`, `Event.VisitorKind` and `EventListOptions.Kind` |
| `AiPurposes`, `AiRuntimes` | `AiAgent.Purpose` (and `EventListOptions.AiPurpose`), `AiAgent.Runtime` |
| `AiVerificationMethods`, `AiVerificationResults` | `AiVerification.Method`, `AiVerification.Result` |
| `AttestationStates`, `AttestationStatuses`, `AttestSkippedReasons` | the archived document's `attestation.state`, `attestation.status` and `attestation.attest_skipped` |
| `VerifyReasons` | `VerifyResult.Reason` |
| `WebhookEventTypes`, `WebhookEnvironments` | `WebhookEvent.Type`, `WebhookEvent.Environment` |

```csharp
bool suppressed = ev.Triggers.Any(t => t.Signal == Signals.FingerprintSuppressed);
```

`EventTrigger.V3Signals` groups the `v: 3` anti-spoofing and integrity
signals: `Signals.AttestationFailed`, `AttestationDowngrade`,
`DeviceInconsistent`, `ReportIncomplete`, `ProbeErrors`,
`FetchMetadataMissing`, `WebTicketMissing`, `WebTicketInvalid`, `Tampering`,
`CustomRootCa` and `SecondaryUserProfile`. The `EventTrigger.Signal…`
constants are the same strings.

### Verify a backend action

Send the browser's `request_id` to your backend and verify it there with a
server-only key, which reads only the events of its own environment.
`VerifyAsync` reads the event and runs these checks in order, stopping at the
first that fails:

| Check | `Reason` (`VerifyReasons`) |
|---|---|
| The event exists | `not_found` (`NotFound`); `Event` is null |
| It is no older than `MaxAgeSeconds` (300 by default); a missing or unreadable `occurred_at` fails | `too_old` (`TooOld`) |
| Its tag equals `Tag`, when you set one | `tag_mismatch` (`TagMismatch`) |
| It has a visitor id (`VisitorId` is `""` when unidentifiable) | `unidentifiable` (`Unidentifiable`) |
| It carries a suspect score | `unscored` (`Unscored`) |
| It was not consumed before | `already_consumed` (`AlreadyConsumed`) |

When every check passes, `Ok` is true and `Reason` is `""`
(`VerifyReasons.Ok`). Consumption comes
last and happens only when every other check passed, so one event backs one
decision; pass `Consume = false` to check without consuming. Score policy
stays with you: `VerifyAsync` never looks at the score, level or triggers.

```csharp
VerifyResult result;
try
{
    result = await fingerly.Events.VerifyAsync(
        requestId, new VerifyOptions { Tag = "checkout" }, ct);
}
catch (FingerlyApiException e) when (e.IsPending)
{
    // Not recorded yet, so nothing is decided: verify again after
    // e.RetryAfter seconds, or asynchronously.
    return "retry";
}

if (!result.Ok) return "refuse"; // result.Reason says why
if (result.Event!.SuspectLevel == SuspectLevels.High) return "review";
return "allow";
```

A pending event (`IsPending`) is not a verdict, so `VerifyAsync` throws it
rather than reporting a reason: retry after `RetryAfter` seconds or verify
asynchronously, rather than refusing the action. Every error other than
`event_not_found` and `event_already_consumed` is thrown unchanged. A
non-positive `MaxAgeSeconds` or an empty `Tag` throws `ArgumentException`
before any request.

`ConsumeAsync(requestId)` consumes an event directly and returns a
`ConsumeResult` (`RequestId`, `ConsumedAt`, `First`). Any later call throws
`FingerlyEventAlreadyConsumedException`, a `FingerlyApiException` with status
`409`, code `event_already_consumed` (`IsEventAlreadyConsumed`) and the
original time in `ConsumedAt`.

Do not authorize an action using scores or visitor identifiers supplied by the
browser. Tags correlate events; they do not replace your application's
authentication. Never put a personal identifier (user ID, email, install ID or
advertising ID) in a tag: the server keeps tags in long-lived rollups.

### Errors

Every exception derives from `FingerlyException`.

```csharp
try
{
    ev = await fingerly.Events.GetAsync(requestId, ct);
}
catch (FingerlyApiException e) when (e.IsPending)
{
    return "retry"; // not decided yet: read again after e.RetryAfter seconds
}
catch (FingerlyApiException e) when (e.IsEventNotFound)
{
    return "refuse"; // never issued, or past retention
}
```

| Exception | Raised when |
| --- | --- |
| `FingerlyApiException` | any non-2xx answer, redirects included. Carries `Status`, and optional `Code`, `Details`, `RequestId` and `RetryAfter` (the seconds `Retry-After` asked for, or null) |
| `FingerlyTimeoutException` | the deadline (`Timeout`) passed |
| `FingerlyConnectionException` | the API is unreachable |
| `OperationCanceledException` | a `CancellationToken` that you cancelled stopped the call |

The secret key is stripped from every field of `FingerlyApiException`, and a
response body is never echoed, not even in an inner exception.

A read answers `404` with one of two codes, each with its own check:

| Check | Code | Meaning |
| --- | --- | --- |
| `IsPending` | `event_pending` | The request id was accepted and the event is still being recorded. Nothing is decided yet: read again after `RetryAfter` seconds (the API sends `Retry-After: 2`), or verify asynchronously. Do not treat it as fraud. |
| `IsEventNotFound` | `event_not_found` | The request id was never issued, or the event is past retention. |

The client never retries by itself, except the
[DeviceCheck calls](#devicecheck-bits). No SDK object prints a credential:
`ToString()` shows `[redacted]` in place of every key, and serializing the
options leaves the keys out.

## DeviceCheck bits

For an iOS identification whose App Attest evidence covered a DeviceCheck
token, Fingerly holds the token for one hour, and your backend spends it once
to read or to write the two bits Apple keeps for the device. The bits survive
a reinstall and mean what you decide; Fingerly never reads them.

```csharp
DeviceCheckBits bits = await fingerly.DeviceCheck.QueryAsync(requestId, ct);
// bits.Found, bits.Bit0, bits.Bit1, bits.LastUpdate ("YYYY-MM" or null)

DeviceCheckWriteResult set = await fingerly.DeviceCheck.WriteAsync(
    requestId, new DeviceCheckWrite { Bit0 = true }, ct);
```

Apple treats a DeviceCheck token as single-use, so a read and a write each
need their own identification. A once-per-device free trial takes two:

1. On the identification that asks for the trial, call `QueryAsync`. If the
   bit says the device already had one, refuse it.
2. Otherwise grant the trial and record the grant in your own data, so it
   holds even before the device is marked.
3. On the next identification from that install (the app can identify again
   right after the grant), call `WriteAsync` with `Bit0 = true`.

```csharp
try
{
    DeviceCheckBits bits = await fingerly.DeviceCheck.QueryAsync(requestId, ct);
    if (bits.Found && bits.Bit0) return "offer-paid-plan";
}
catch (FingerlyApiException e) when (e.Code == DeviceCheckErrors.NotHeld)
{
    // No token for this event: decide without the bits.
}
```

A write needs `Bit0`, `Bit1` or both; one with neither throws
`ArgumentException` and sends nothing. Failures are `FingerlyApiException`;
compare `Code` with the `DeviceCheckErrors` constants:

| Status | `DeviceCheckErrors` | Code |
|---|---|---|
| 404 | `NotHeld` | `device_check_not_held` |
| 409 | `Spent` | `device_check_spent` |
| 409 | `Busy` | `device_check_busy` |
| 409 | `CredentialUnavailable` | `credential_unavailable` |
| 422 | `BitsRequired` | `bits_required` |
| 422 | `AppleRejected` | `apple_rejected`, with Apple's status in `AppleStatus` |
| 503 | `Unavailable` | `device_check_unavailable` |

These two calls are the only ones the client retries: after a `503` or a
transport failure or timeout it sends the identical request again, up to two
more times. Apple lets a token be retried only for the same request, and the
API answers `409 device_check_spent` to a retry with the other operation or
other bits. No other answer is retried. Never retry with a different request
yourself; after a `device_check_spent`, use another identification.

## Verify webhooks

The handler is in [Getting started](#getting-started). Verify the exact raw
bytes before parsing them. Reading the body into a `string` and encoding it
again is not the same thing: any byte that is not valid UTF-8 becomes U+FFFD on
the way through, and the digest then never matches.

| Behaviour | Detail |
| --- | --- |
| What a signature covers | the timestamp and the body |
| Replay tolerance | five minutes by default (`Webhooks.DefaultTolerance`) |
| Secret rotation | any matching signature in the comma-separated header is accepted; at most 32 are read |
| Malformed or missing input | a `false` result, never an exception |
| Deduplication | persist processed `Id` values to deduplicate deliveries |

`WebhookEvent.Data` stays a `JsonElement`, so a new event type needs no SDK
upgrade. `identification.completed` and `visitor.suspect` carry the event's
project as `data.project_id`; `identification.refused` carries it only when the
refused request named a known key. Read it with
`Data.TryGetProperty("project_id", out var projectId)`.

```csharp
switch (delivery.Type)
{
    case WebhookEventTypes.VisitorSuspect:
        await ReviewAsync(delivery.Data, ct);
        break;
    case WebhookEventTypes.IdentificationCompleted:
        await RecordAsync(delivery.Data, ct);
        break;
    default:
        break; // a type added later: acknowledge it and move on
}
```

## First-party proxy (same-origin)

Content blockers refuse browser requests to third-party APIs, and a refused
identification is a visitor never identified. Mount the proxy on a path of your
own site and those requests become first-party. The proxy key is what lets that
hop declare the visitor's real address, origin and user agent; a public key
alone never can, so nobody can spoof them.

Create a proxy in the dashboard (**Integration > Proxy keys**). It issues the
key and a random path such as `/k7x2qm4a`; use that path as it is.

```csharp
builder.Services.AddFingerlyProxy(options =>
{
    options.ProxyKey = builder.Configuration["Fingerly:ProxyKey"]!;
    options.Path = "/k7x2qm4a";
    options.ClientIp = request => request.Header("CF-Connecting-IP") ?? request.RemoteAddress;
});
```

> **`ClientIp` is the one thing you must get right.** Take the header your CDN
> or load balancer sets when it is there, and the connection's peer address
> (`RemoteAddress`) otherwise. Trust that header only when traffic can reach
> your server through nothing but that CDN or load balancer. Never read the
> left-most `X-Forwarded-For` value: the visitor writes it.

`ClientIp` must return exactly one IPv4 or IPv6 address. Anything else (null,
empty, a list, a port, a host name, or an exception) is a configuration error:
the request is refused with a 500 saying "The client IP callback returned no
valid address", nothing is sent upstream, and the mistake is logged once.

Then mount it, either as a routed endpoint:

```csharp
app.MapFingerlyProxy();
```

or as middleware, which suits an application that does not use endpoint
routing:

```csharp
app.UseFingerlyProxy();
```

Prefer the mapped endpoint in a minimal-API application: the path becomes an
ordinary endpoint, so rate limiting, CORS and output caching apply to it the
way they apply to everything else. It is mapped with `AllowAnonymous()`, so a
fallback authorization policy does not send signed-out visitors to a sign-in
page: the browser SDK carries no user. Put the middleware ahead of
`UseAuthentication()` for the same reason.

Then point the browser SDK at the same path:

```ts
const fingerly = await load({ apiKey: 'fly_pk_…', endpoints: '/k7x2qm4a' })
```

### What the proxy does

| Rule | Behaviour |
| --- | --- |
| Upstream | Client SDKs post a single identification envelope to the one path, and the proxy forwards its raw bytes verbatim to Fingerly's one ingest URL, `https://api.fingerly.io/api/v1/ingest` (`ApiVersion` changes the version there and nowhere else). |
| Upstream origin | `Upstream` overrides the API origin and takes a bare origin only: `https://host` or `https://host:port`, with no credentials, path, query, fragment, space or control character. Plain HTTP is refused, except for `http://127.0.0.1`, `http://localhost` or `http://[::1]` (any port) when `AllowInsecureLoopback` is set, which is for a local test harness. |
| Path match | Only `POST` (plus `OPTIONS` when `AllowedOrigins` is configured; the method is case-sensitive, so `post` is refused) to the path itself, exactly: case-sensitive, and compared with the raw request target as the visitor sent it (the text after the authority for an absolute-form target, not normalised), so a trailing or doubled slash, a dot segment or percent encoding is a different path. The middleware passes such a request on to your application; the mapped endpoint answers it 404. Anything under the path gets the same. A path routing does not match at all never reaches the mapped endpoint and is your application's to answer. |
| Other methods | `405` with `Allow`. Both adapters write that `404` and `405` themselves, with `Cache-Control: no-store`: the mapped endpoint takes every method so that routing never answers a wrong one. |
| Event reads | Need a secret key and stay unreachable from a browser by construction. |
| Proxy key | `ProxyKey` is required and must be a `fly_px_{production\|staging\|development}_…` key: a missing or malformed one throws `ArgumentException` when the proxy is built. |
| Public key | On every request the caller's `X-API-Key` must be a public key, `fly_pk_…`, from the same environment as the proxy key. Anything else (no header, another environment, a secret `fly_sk_` key) gets `401` as `text/plain; charset=utf-8` with `Cache-Control: no-store`, and nothing is forwarded. Upstream receives `X-Fingerly-Proxy-Key` plus that public key as `X-API-Key`; a secret key is never sent. |
| Origin | A request without either `Origin` or a validated native `X-Fingerly-SDK-Platform` header (listed below) is rejected locally with `400` (`missing_origin`), and `X-Fingerly-Origin` is never synthesized from `Host`, `Referer` or the request URL. Only one channel goes upstream: when `Origin` is present it is sent as `X-Fingerly-Origin` and the platform header is dropped, as the API's direct path treats such a request as a browser one. |
| Body size | Bodies over 1 MiB get `413`, enforced while reading rather than after buffering. |
| Forwarded upstream | All incoming `X-Fingerly-*`, `Cookie`, `Authorization` and trace-context headers are stripped, restoring only a validated `X-Fingerly-SDK-Platform` (for a request without `Origin`) alongside `X-Fingerly-Host`, `Idempotency-Key`, `Sec-Fetch-*`, `Sec-CH-UA-*` and the Web Bot Auth `Signature`, `Signature-Input` and `Signature-Agent` (each dropped when over 4096 bytes). |
| Proxy identity | The proxy reports itself upstream as `dotnet/0.4.1`, which the dashboard shows per proxy. |
| Relayed back | Only `Content-Type`, `X-Request-Id`, `RateLimit-Limit`, `RateLimit-Remaining` and `Retry-After`, with `Cache-Control: no-store` added. |
| Upstream timeout | `504` ("Gateway timeout") if the upstream did not answer within `Timeout` (5 seconds by default). |
| Other upstream failures | `502` ("Bad gateway") for anything else: refused, reset, truncated, larger than 4 MiB, undecodable, a redirect (never followed), or a status outside 200 to 599. |
| Visitor disconnects | The upstream call is cancelled, and the request ends quietly. |

The native platform values accepted in `X-Fingerly-SDK-Platform` are `ios`,
`android`, `react-native`, `react-native-ios`, `react-native-android`,
`flutter`, `flutter-ios`, `flutter-android`, `lynx-ios`, `lynx-android`,
`capacitor` and `kotlin-multiplatform`.

The response headers are an allowlist because a blocker can match a response
header by name: nothing on the response names Fingerly, whatever the API adds
later. The answer is read whole before anything is relayed, so a truncated one
is never passed on as a success.

One line is logged per failure, naming the kind of failure only, without a
stack trace, a key or the upstream's address:
`AddFingerlyProxy` sends it to the application's `ILogger` (category
`Fingerly.FingerlyProxy`) at warning level, and `FingerlyProxyOptions.Log`
takes any other sink.

<details>
<summary>Path, middleware and routing details</summary>

The middleware asks `proxy.Handles(path)` before reading anything. It sees
every request in the pipeline, and calling `HandleAsync` to find out whether
one belongs to Fingerly would have consumed a request body the application
still needs. In ASP.NET Core that body is a forward-only stream unless the
application opted into buffering, so the mistake would be silent rather than
loud. For the same reason the middleware refuses a proxy with no `Path`.

`Path` is the path within your application. It excludes anything
`UsePathBase` or an IIS virtual directory adds, because the proxy matches
`Request.Path`, which is application-relative in every host. It is optional
where routing already decides the path: leave it unset and give the path to
`MapFingerlyProxy` instead, which also works inside a route group.

```csharp
app.MapGroup("/shop").MapFingerlyProxy("/k7x2qm4a");
```

Setting it in both places is refused, so the two cannot disagree. Use the path
the dashboard issued rather than one of your own: blockers look for words such
as `fingerprint`, `tracking` and `analytics`.

</details>

### Outside ASP.NET Core

`FingerlyProxy` speaks to no server API. It reads a `ProxyRequest` (with
`RemoteAddress` set to the connection's peer) and returns a `ProxyResponse`,
so an isolated-worker Azure Function, a YARP transform or an OWIN pipeline can
mount it by translating those two shapes. Where that host routes only the one
path to it, `Path` can stay unset. That is also why the core still runs on
.NET Standard 2.0.

## AI crawlers on your server

AI crawlers and fetchers such as GPTBot or ClaudeBot mostly fetch pages
without running their JavaScript, so the browser SDK never sees them.
`AgentSightings` reports the requests your server served to Fingerly, which
says what sent each one, and remembers the answers so your code can ask. The
console shows the reported traffic by agent.

```csharp
builder.Services.AddFingerly(options => options.SecretKey = builder.Configuration["Fingerly:SecretKey"]!);
builder.Services.AddFingerlyAgentSightings(options =>
{
    // The same trusted address the proxy uses. Behind a CDN, its header;
    // otherwise the connection's peer.
    options.ClientIp = request => request.Header("CF-Connecting-IP") ?? request.RemoteAddress;
});
var app = builder.Build();
app.UseFingerlyAgentSightings(); // first, so it sees every response
```

By default it reports every response with a `text/html` or
`application/xhtml+xml` content type, and every request for `/robots.txt`,
`/llms.txt` and `/llms-full.txt`, whatever its status. Static assets, API
routes and health checks are left out unless you add them. Change that with
`ContentTypes` (replaces the default list), `IncludePathPrefixes` and
`ExcludePathPrefixes` (an exclusion wins). Which responses are reported depends
on your own path and content type only, never on the user agent or anything
else the visitor sent.

Ask what sent a request with `Lookup`, and compare against the named values:

```csharp
app.MapGet("/articles/{id}", (string id, HttpContext context, AgentSightings sightings) =>
{
    AgentAnswer answer = sightings.Lookup(context);
    if (answer.VisitorKind == VisitorKinds.AiBot && answer.Ai?.Purpose == AiPurposes.Training)
    {
        // Your own policy for training crawlers.
    }
    return Results.Ok(id);
});
```

`Lookup` answers from memory only and never throws. Until the server has
answered for that user agent, address and `Signature-Agent`, or once that
answer has expired, it returns `AgentAnswer.Unknown` (`IsUnknown` is true, and
its `VisitorKind` is empty, so it matches none of `VisitorKinds`): serve the
request as usual.
A signed request is checked on every report, so its answer is never
remembered. The label comes from what the request claims and from the checks
in `answer.Ai.Verification`; read it as a label, not as proof.

What happens behind it:

- **Never in the way.** The middleware reports in an `OnCompleted` callback,
  after the response has been sent, and in memory only. A background timer
  sends the reports, in batches of up to 200, when 200 are pending, when they
  reach 64 KiB, or 2 seconds after the oldest, one batch at a time, each within
  5 seconds. The timer does not keep the process alive.
- **Bounded memory.** At most `Capacity` answers (4096 by default, about
  1.5 MB; a larger value is held to 65536, about 25 MB), least recently used
  first out, and at most 1000 requests pending or in flight. Repeats of the
  same request are sent once with a count. When 1000 are held, a new request
  is not reported and `DroppedCount` goes up.
- **Fail open.** A refused or failed report never reaches a visitor. A rate
  limit pauses reporting until `Retry-After`; a wrong or revoked key drops the
  batch and pauses for a minute; a `503` or an API that cannot be reached is
  retried with backoff from 1 to 60 seconds; any other refusal, and a report
  that timed out after it was sent, is dropped, so nothing is counted twice.
  Each is logged at most once a minute through `ILogger`, and so is a
  `ClientIp` that gave no single address.
- **What is sent.** For each request: the `User-Agent` (a value over 4096
  bytes is not reported), the address from `ClientIp`, the `Host`, the method,
  the `Signature`, `Signature-Input` and `Signature-Agent` headers when present,
  and a count. Never the path, the query string, cookies, `Authorization`,
  other headers, the body or the response, and none of these is logged.
- **Shutdown.** Disposing `AgentSightings`, as the host does when it stops,
  sends what is pending within 5 seconds. Where a process can end without
  warning, such as a serverless function, call `await sightings.FlushAsync()`
  before it returns.

Outside ASP.NET Core, build `new AgentSightings(fingerly, options)` once, call
`sightings.Report(request, responseContentType)` with a `ProxyRequest` after
each response, and `sightings.Lookup(request)` wherever you need the answer.
`fingerly.Agents.ReportSightingsAsync` is the single call underneath, for a
host that batches by itself.

## Deadlines, limits and connections

`Timeout` bounds the whole exchange, including receiving the body: a server
that sends headers and then trickles cannot hold the call past it. It is
applied with a linked `CancellationTokenSource` rather than by writing to
`HttpClient.Timeout`, so handing the client a shared `HttpClient` does not
rewrite a timeout that belongs to whoever else is using it.

The two reasons a call can stop early stay distinguishable. A deadline throws
`FingerlyTimeoutException`; a `CancellationToken` that you cancelled raises
`OperationCanceledException`, which is what the caller who cancelled expects
to catch. `HttpClient` reports both as `TaskCanceledException` on its own, so
telling them apart is the SDK's job rather than yours.

An answer larger than 4 MiB is refused rather than buffered, whatever
`HttpClient` sent it: the client throws `FingerlyException`, the proxy answers
502.

Every client and proxy in a process shares one connection pool, so building
one per request still reuses connections, and the pool recycles connections
every two minutes so DNS changes are picked up. It keeps no cookie jar, never
follows a redirect, and copies none of your application's `traceparent`,
`tracestate` or `baggage` onto requests to Fingerly. `AddFingerly` configures
the `IHttpClientFactory` handler the same way. If you pass your own
`Handler`, give it `AllowAutoRedirect = false`, `UseCookies = false`, automatic
decompression and
`ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator()`.
The proxy refuses, at construction, a `SocketsHttpHandler` or
`HttpClientHandler` (directly or under a `DelegatingHandler`) left at
`AllowAutoRedirect = true`, and answers 502 to any redirect or to an answer
for another URL whatever the handler.

## Requirements

| Package | Target frameworks |
| --- | --- |
| `Fingerly.Server` | `net8.0`, `netstandard2.0` (reachable from .NET Framework 4.6.2 and newer) |
| `Fingerly.Server.AspNetCore` | `net8.0` |

## Development and release preparation

<details>
<summary>Build, test and pack commands, test approach, source structure</summary>

```sh
dotnet build                                   # both target frameworks, warnings as errors
dotnet test                                    # against a real Kestrel server
dotnet pack -c Release -o artifacts
dotnet publish tools/TrimCheck -c Release -r osx-arm64 -o trimmed && ./trimmed/TrimCheck

FINGERLY_CONTRACT_DIR=../../contract dotnet run --project tools/SyncContract
```

Warnings are errors, and XML documentation is generated, so an undocumented
public member fails the build the way an unused variable does. A warning the
SDK ships is one its users cannot fix.

The tests drive a real Kestrel server rather than a stubbed
`HttpMessageHandler` or `HttpContext`. A double shares the SDK's assumptions
about framing, path bases and bodies that can only be read once, so it cannot
disprove any of them.

The vendored synthetic contract snapshot makes tests independent of sibling
repositories. Its hashes are checked by `ContractTest`, so there is no separate
check step. Never put production secrets in fixtures.

### Source structure

`FingerlyClient` holds the options; `EventsService` owns the reads and the list
filters, and `Event`, `EventPage` and `EventListOptions` are what they speak
in; it also verifies and consumes events, with `VerifyOptions`,
`VerifyResult`, `VerifyReasons` and `ConsumeResult`. `Signals` and
`NamedValues.cs` hold the named string values. `DeviceCheckService` spends held DeviceCheck tokens, with
`DeviceCheckBits`, `DeviceCheckWrite` and `DeviceCheckWriteResult`. `Webhooks` verifies signatures. `FingerlyProxy` holds every proxy decision,
with `ProxyRequest` and `ProxyResponse` as the neutral shapes the hosts
translate to. `AgentsService` sends one report of served requests;
`AgentSightings` batches them and remembers the answers, with `Sighting`
computing the cache key. `SharedHttp` is the process-wide connection pool and reads every
answer within its limit. `Keys` validates every caller-supplied option before
any network access, and `Json` keeps the serializer context in one place so a
caller's own `JsonSerializerOptions` cannot change how responses are read.

</details>

## Documentation

- [.NET SDK guide](https://docs.fingerly.io/docs/sdks/dotnet)
- [Webhooks](https://docs.fingerly.io/docs/webhooks)
- [Proxy integrations](https://docs.fingerly.io/docs/proxy-integrations)
- [API reference](https://docs.fingerly.io/reference/overview)

## Help

Open an issue on [this repository](https://github.com/fingerly-io/sdk-dotnet/issues), or write to
[support@fingerly.io](mailto:support@fingerly.io). Report security issues to
[security@fingerly.io](mailto:security@fingerly.io).

## Licence

Apache-2.0. See [LICENSE](https://github.com/fingerly-io/sdk-dotnet/blob/main/LICENSE).
