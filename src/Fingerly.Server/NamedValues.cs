namespace Fingerly;

// The closed string sets the API sends, as constants. The properties on the
// models stay strings, so a value the server adds after this SDK was released
// still deserializes; these are only names for the values known today. They
// are const rather than a wrapper type, so they work as switch labels and add
// nothing to the source-generated JSON context.

/// <summary>
/// The <see cref="Event.SuspectLevel"/> bands, as the exact strings the API
/// sends. Also what <see cref="EventListOptions.Level"/> accepts.
/// </summary>
/// <remarks>
/// <code>
/// if (ev.SuspectLevel == SuspectLevels.High) return "review";
/// </code>
/// </remarks>
public static class SuspectLevels
{
    /// <summary>The lowest band.</summary>
    public const string Low = "low";

    /// <summary>The middle band.</summary>
    public const string Medium = "medium";

    /// <summary>The highest band.</summary>
    public const string High = "high";
}

/// <summary>The <see cref="EventTrigger.Confidence"/> values, as the exact strings the API sends.</summary>
public static class Confidences
{
    /// <summary>Low confidence.</summary>
    public const string Low = "low";

    /// <summary>Medium confidence.</summary>
    public const string Medium = "medium";

    /// <summary>High confidence.</summary>
    public const string High = "high";
}

/// <summary>
/// The server's verdict on a native event's hardware evidence, as the exact
/// strings of <c>attestation.state</c> in <see cref="Event.Document"/>.
/// </summary>
public static class AttestationStates
{
    /// <summary>The hardware evidence verified.</summary>
    public const string Attested = "attested";

    /// <summary>No evidence that could be proven was offered.</summary>
    public const string NotAttested = "not_attested";

    /// <summary>The evidence failed verification.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// The detail behind <see cref="AttestationStates"/>, as the exact strings of
/// <c>attestation.status</c> in <see cref="Event.Document"/>.
/// </summary>
public static class AttestationStatuses
{
    /// <summary>The evidence verified.</summary>
    public const string Verified = "verified";

    /// <summary>The evidence could not be checked either way.</summary>
    public const string Indeterminate = "indeterminate";

    /// <summary>The evidence was rejected.</summary>
    public const string Invalid = "invalid";

    /// <summary>The report carried no evidence.</summary>
    public const string Absent = "absent";

    /// <summary>The key has nothing configured to verify the evidence against.</summary>
    public const string Unconfigured = "unconfigured";
}

/// <summary>
/// Why a native SDK sent no attestation, as the exact strings of
/// <c>attestation.attest_skipped</c> in <see cref="Event.Document"/>. The app
/// reports it about itself; it is diagnostic only and never changes the score.
/// </summary>
public static class AttestSkippedReasons
{
    /// <summary>
    /// The challenge offered no attestation: on iOS the SDK key has no Apple app ID for
    /// App Attest, on Android the app's transport returned no challenge offer.
    /// </summary>
    public const string NotOffered = "not_offered";

    /// <summary>
    /// The device cannot make an attested key: App Attest is not supported on the iOS
    /// device, or Android is older than 7.0 (API 24).
    /// </summary>
    public const string Unsupported = "unsupported";

    /// <summary>
    /// No usable challenge ticket: the challenge request failed, or on Android the ticket
    /// it returned was malformed.
    /// </summary>
    public const string ChallengeUnavailable = "challenge_unavailable";

    /// <summary>
    /// The platform key store failed to create, attest or sign with the key, or on Android
    /// the stored key record could not be read.
    /// </summary>
    public const string KeyError = "key_error";

    /// <summary>
    /// Attestation did not finish in time: a key store call ran past its budget, or another
    /// identification held the attestation step too long.
    /// </summary>
    public const string Timeout = "timeout";

    /// <summary>
    /// The server rejected a new key from this install (on iOS, also when it asked for a
    /// replacement), and the SDK is waiting out a backoff before it registers another.
    /// </summary>
    public const string Backoff = "backoff";

    /// <summary>
    /// The report has no usable install ID, or on iOS the stored key record could not be
    /// read or saved.
    /// </summary>
    public const string InstallUnavailable = "install_unavailable";
}

/// <summary>
/// The <see cref="WebhookEvent.Type"/> values, as the exact strings the API sends.
/// </summary>
/// <remarks>
/// <code>
/// switch (webhook.Type)
/// {
///     case WebhookEventTypes.VisitorSuspect:
///         Review(webhook.Data);
///         break;
///     default:
///         break; // a type added later
/// }
/// </code>
/// </remarks>
public static class WebhookEventTypes
{
    /// <summary>An identification finished.</summary>
    public const string IdentificationCompleted = "identification.completed";

    /// <summary>An identification request was refused.</summary>
    public const string IdentificationRefused = "identification.refused";

    /// <summary>A visitor scored as suspect.</summary>
    public const string VisitorSuspect = "visitor.suspect";

    /// <summary>The organization's billing status changed.</summary>
    public const string BillingStatusChanged = "billing.status_changed";

    /// <summary>A day's usage was settled.</summary>
    public const string UsageDailySettled = "usage.daily_settled";

    /// <summary>A test delivery sent on request to one endpoint. Nobody subscribes to it.</summary>
    public const string WebhookTest = "webhook.test";
}

/// <summary>The <see cref="WebhookEvent.Environment"/> values, as the exact strings the API sends.</summary>
public static class WebhookEnvironments
{
    /// <summary>Sent for production traffic.</summary>
    public const string Live = "live";

    /// <summary>Sent for staging and development traffic.</summary>
    public const string Test = "test";
}

/// <summary>The <see cref="Event.State"/> values, as the exact strings the API sends.</summary>
/// <remarks>
/// A stored event is <see cref="Enriched"/> or <see cref="Unavailable"/>. An
/// event still being recorded is not returned as a row: the read throws a
/// <see cref="FingerlyApiException"/> whose
/// <see cref="FingerlyApiException.IsPending"/> is true.
/// </remarks>
public static class EventStates
{
    /// <summary>Accepted and not yet recorded.</summary>
    public const string Pending = "pending";

    /// <summary>The submission was enriched and stored.</summary>
    public const string Enriched = "enriched";

    /// <summary>Enrichment produced nothing; <see cref="Event.Reason"/> says why.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>The <see cref="Event.Environment"/> values, as the exact strings the API sends.</summary>
public static class Environments
{
    /// <summary>Production.</summary>
    public const string Production = "production";

    /// <summary>Staging.</summary>
    public const string Staging = "staging";

    /// <summary>Development.</summary>
    public const string Development = "development";
}

/// <summary>
/// The <see cref="Event.Platform"/> scoring profiles, as the exact strings the
/// API sends. <see cref="Event.SdkPlatform"/> is what the SDK called itself and
/// is not one of these.
/// </summary>
public static class Platforms
{
    /// <summary>Browsers.</summary>
    public const string Web = "web";

    /// <summary>Android apps.</summary>
    public const string Android = "android";

    /// <summary>iOS apps.</summary>
    public const string Ios = "ios";
}

/// <summary>
/// The <see cref="Event.AnonymityNetwork"/> values, as the exact strings the
/// API sends. The property is absent on an ordinary connection.
/// </summary>
public static class AnonymityNetworks
{
    /// <summary>A Tor exit node.</summary>
    public const string Tor = "tor";

    /// <summary>A privacy relay such as Apple's Private Relay.</summary>
    public const string Relay = "relay";

    /// <summary>A reported VPN endpoint.</summary>
    public const string Vpn = "vpn";

    /// <summary>A proxy.</summary>
    public const string Proxy = "proxy";

    /// <summary>A hosting or datacenter network.</summary>
    public const string Hosting = "hosting";
}

/// <summary>The <see cref="Event.DeviceKind"/> values, as the exact strings the API sends.</summary>
public static class DeviceKinds
{
    /// <summary>A desktop or laptop.</summary>
    public const string Desktop = "desktop";

    /// <summary>A phone.</summary>
    public const string Mobile = "mobile";

    /// <summary>A tablet.</summary>
    public const string Tablet = "tablet";

    /// <summary>A client with no device at all.</summary>
    public const string Server = "server";
}

/// <summary>The <see cref="Event.VisitorKind"/> values, as the exact strings the API sends.</summary>
public static class VisitorKinds
{
    /// <summary>A browser or an app.</summary>
    public const string Human = "human";

    /// <summary>A named agent collecting or answering on your content.</summary>
    public const string AiBot = "ai_bot";

    /// <summary>A driver, headless browser or scripted client.</summary>
    public const string Automation = "automation";

    /// <summary>A named search engine's crawler.</summary>
    public const string SearchCrawler = "search_crawler";
}
