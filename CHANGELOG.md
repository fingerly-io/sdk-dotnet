# Changelog

## 0.1.0 (unreleased)

First release: event reads, webhook verification and the same-origin proxy,
with ASP.NET Core middleware, endpoint routing and dependency injection.

Changes made before the first release, from the pre-release field test:

- One connection pool per process, with no cookie jar and no trace or baggage
  propagation; `AddFingerly`'s factory handler is configured the same way.
- `MapFingerlyProxy` maps an anonymous endpoint, so a fallback authorization
  policy cannot redirect signed-out visitors.
- An upstream timeout is `504 Gateway timeout`; any other upstream failure is
  `502 Bad gateway`; one line is logged per failure
  (`FingerlyProxyOptions.Log`, or the application's `ILogger` through
  `AddFingerlyProxy`). A visitor who disconnects ends the request quietly.
- Answers are capped at 4 MiB for any `HttpClient` and read whole under one
  deadline before being relayed.
- `ClientIp` must return exactly one IP address; `ProxyRequest.RemoteAddress`
  carries the connection's peer, filled by the middleware and the endpoint.
- The proxy path is matched exactly, against the raw request target.
- `FingerlyApiException.IsPending` for `404 event_pending`: the event was
  accepted and is still being recorded, so read again after `RetryAfter`
  seconds. `IsEventNotFound` still matches only `event_not_found` (never
  issued, or past retention).
- `FingerlyApiException.RetryAfter`, and the request id from `X-Request-Id`
  when the body has none; response bodies are never echoed.
- Webhook verification reads at most 32 signature candidates.
- `ToString()` of every SDK object redacts credentials; the options serialize
  without their keys.
- Both packages carry `README.md` and `LICENSE`.
