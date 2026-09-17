# fingerly-dotnet

Server-side event reads, webhook verification and a same-origin proxy for
.NET. Version 0.1.0 is prepared for release; the repository is private and
nothing has been published to NuGet.

```sh
dotnet add package Fingerly.Server
dotnet add package Fingerly.Server.AspNetCore   # for the proxy
```

```csharp
using Fingerly;

builder.Services.AddFingerly(options =>
{
    options.SecretKey = builder.Configuration["Fingerly:SecretKey"]!;
});

// Anywhere a FingerlyClient is injected:
Event ev = await fingerly.Events.GetAsync(requestId, cancellationToken);
EventPage page = await fingerly.Events.ListAsync(
    new EventListOptions { Level = "high", Limit = 50 }, cancellationToken);
```

Event reads require `fly_sk_{eu|us}_{production|staging|development}_…` keys.
The key selects the regional endpoint; `Endpoint` overrides it for a local
API. Public, proxy, management, malformed and unsupported keys are rejected
before network access, by the constructor rather than by the first call.
Requests do not follow redirects. `GetAsync` and `ListAsync` each perform one
request and never retry.

## Packages

| Package | Contains | For |
| --- | --- | --- |
| `Fingerly.Server` | client, webhooks, proxy core | everything |
| `Fingerly.Server.AspNetCore` | middleware, endpoint routing, DI | the proxy |

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

## Everything is asynchronous

There is no synchronous counterpart to any call, on purpose. Blocking a
request thread on I/O is what exhausts a thread pool under load, and the
deadlock that `.Result` can cause is not one an SDK could warn about at the
call site.

## Deadlines and cancellation

`Timeout` bounds the whole exchange, including receiving the body. It is
applied with a linked `CancellationTokenSource` rather than by writing to
`HttpClient.Timeout`, so handing the client a shared `HttpClient` does not
rewrite a timeout that belongs to whoever else is using it.

The two reasons a call can stop early stay distinguishable. A deadline throws
`FingerlyTimeoutException`; a `CancellationToken` that you cancelled raises
`OperationCanceledException`, which is what the caller who cancelled expects
to catch. `HttpClient` reports both as `TaskCanceledException` on its own, so
telling them apart is the SDK's job rather than yours.

## Errors

Every exception derives from `FingerlyException`.

```csharp
try
{
    ev = await fingerly.Events.GetAsync(requestId, ct);
}
catch (FingerlyApiException e) when (e.Status == 404)
{
    return "refuse";
}
```

`FingerlyApiException` carries `Status`, and optional `Code`, `Details` and
`RequestId`. The secret key is stripped from all three, and the response body
is never echoed. `IsEventNotFound` names the one retryable read failure, so it
needs no status-and-code check of your own. A deadline throws
`FingerlyTimeoutException`; an unreachable API throws
`FingerlyConnectionException`.

## Unscored events

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

`Raw` keeps the response body exactly as it arrived, so a field the API adds
later is reachable without an SDK upgrade.

## Wait for a fresh identification

Identification answers before the event is archived, so reach for this only
when you have just identified a visitor:

```csharp
Event ev = await fingerly.Events.WaitForAsync(
    requestId, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250), ct);
```

Only `IsEventNotFound` is retried; every other failure is thrown at once. When
the event is still missing at the deadline, `FingerlyTimeoutException` keeps
the last `FingerlyApiException` as its inner exception, so the reason it kept
waiting is not lost.

## Verify a backend action

Read the event with a server-only key. Check that its tag matches your action,
that it is recent, that its environment is correct, and that it was scored.
Apply your own policy to the server's verdict. Do not authorize an action using
scores or visitor identifiers supplied by the browser. Tags correlate events;
they do not replace your application's authentication or replay protection.

## Webhooks

```csharp
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
await queue.EnqueueAsync(ev.Id, payload, ct);   // deduplicate on the event ID
```

Verify the exact raw bytes before parsing them. Reading the body into a
`string` and encoding it again is not the same thing: any byte that is not
valid UTF-8 becomes U+FFFD on the way through, and the digest then never
matches. Signatures cover the timestamp and body. The default replay tolerance
is five minutes; persist processed `Id` values to deduplicate deliveries.
During signing-secret rotation, any matching signature in the comma-separated
header is accepted. Malformed or missing input is a false result, never an
exception.

`WebhookEvent.Data` stays a `JsonElement`, so a new event type needs no SDK
upgrade.

## Same-origin proxy

Content blockers refuse browser requests to third-party APIs, and a refused
identification is a visitor never identified. Mount the proxy on a path of your
own site and those requests become first-party. The proxy key is what lets that
hop declare the visitor's real address, origin and user agent; a public key
alone never can, so nobody can spoof them.

```csharp
builder.Services.AddFingerlyProxy(options =>
{
    options.ProxyKey = builder.Configuration["Fingerly:ProxyKey"]!;
    options.Prefix = "/metrics";
    options.ClientIp = request => request.Header("CF-Connecting-IP");
});
```

Then mount it, either as routed endpoints:

```csharp
app.MapFingerlyProxy();
```

or as middleware, which suits an application that does not use endpoint
routing:

```csharp
app.UseFingerlyProxy();
```

Prefer the mapped endpoints in a minimal-API application: the two routes become
ordinary endpoints, so rate limiting, CORS and output caching apply to them the
way they apply to everything else. Either way, put it ahead of authentication:
the browser SDK carries no user, and a policy that redirected it to a sign-in
page would break identification for every signed-out visitor.

Then point the browser SDK at the same path:

```ts
const fingerly = await load({ apiKey: 'fly_pk_…', endpoints: '/metrics' })
```

### What the proxy does

It forwards only `POST` to `{prefix}/api/v1/identify` and
`{prefix}/api/v1/events/{request_id}/supplement`. Anything else gets `404`, and
any other method gets `405`. Event reads need a secret key and stay unreachable
from a browser by construction.

The browser must present a public key from the same region and environment as
the proxy key, or the request gets `401`. Bodies over 1 MiB get `413`, enforced
while reading rather than after buffering. Responses are marked `no-store`.
Only the content type, the visitor metadata Fingerly needs, and an
`Idempotency-Key` are forwarded; customer cookies and authorization headers
never are.

The middleware asks `proxy.Handles(path)` before reading anything. It sees
every request in the pipeline, and calling `HandleAsync` to find out whether
one belongs to Fingerly would have consumed a request body the application
still needs. In ASP.NET Core that body is a forward-only stream unless the
application opted into buffering, so the mistake would be silent rather than
loud.

`Prefix` is the path within your application. It excludes anything
`UsePathBase` or an IIS virtual directory adds, because the proxy matches
`Request.Path`, which is application-relative in every host. A prefix that
`app.Map` has already stripped works too: the path is accepted as either
carrying the prefix or having had it removed. Choose a neutral one. Blockers
look for words such as `fingerprint`, `tracking` and `fingerly`.

> **`ClientIp` is the one thing you must get right.** Resolve it from a header
> your own infrastructure sets, such as your load balancer's or CDN's. Never
> read the left-most `X-Forwarded-For` value: the visitor writes it.

### Outside ASP.NET Core

`FingerlyProxy` speaks to no server API. It reads a `ProxyRequest` and returns
a `ProxyResponse`, so an isolated-worker Azure Function, a YARP transform or an
OWIN pipeline can mount it by translating those two shapes. That is also why
the core still runs on .NET Standard 2.0.

## Development and release preparation

```sh
dotnet build                                   # both target frameworks, warnings as errors
dotnet test                                    # 145 tests against a real Kestrel server
dotnet pack -c Release -o artifacts

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

## Source structure

`FingerlyClient` holds the connection and the options; `EventsService` owns the
reads and the polling loop, and `Event`, `EventPage` and `EventListOptions` are
what they speak in. `Webhooks` verifies signatures. `FingerlyProxy` holds every
proxy decision, with `ProxyRequest` and `ProxyResponse` as the neutral shapes
the hosts translate to. `Keys` validates every caller-supplied option before
any network access, and `Json` keeps the serializer context in one place so a
caller's own `JsonSerializerOptions` cannot change how responses are read.
