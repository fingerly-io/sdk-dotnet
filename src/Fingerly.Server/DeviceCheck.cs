using System.Text.Json.Serialization;

namespace Fingerly;

/// <summary>
/// The two DeviceCheck bits Apple keeps for an event's device, as a read
/// returned them. The bits mean what you decide; Fingerly never reads them.
/// </summary>
public sealed class DeviceCheckBits
{
    /// <summary>False when Apple holds no bits for the device yet; both bits are then false.</summary>
    [JsonPropertyName("found")]
    public bool Found { get; init; }

    /// <summary>The first bit.</summary>
    [JsonPropertyName("bit0")]
    public bool Bit0 { get; init; }

    /// <summary>The second bit.</summary>
    [JsonPropertyName("bit1")]
    public bool Bit1 { get; init; }

    /// <summary>When the bits last changed, as <c>YYYY-MM</c>, or null when Apple holds none.</summary>
    [JsonPropertyName("last_update")]
    public string? LastUpdate { get; init; }

    /// <inheritdoc/>
    public override string ToString() =>
        "DeviceCheckBits[found=" + Found + ", bit0=" + Bit0 + ", bit1=" + Bit1 + "]";
}

/// <summary>
/// The bits a DeviceCheck write sets. A bit left null keeps its value; at
/// least one must be set.
/// </summary>
public sealed class DeviceCheckWrite
{
    /// <summary>The value to set for the first bit, or null to keep it.</summary>
    [JsonPropertyName("bit0")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Bit0 { get; init; }

    /// <summary>The value to set for the second bit, or null to keep it.</summary>
    [JsonPropertyName("bit1")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Bit1 { get; init; }
}

/// <summary>The bits a DeviceCheck write set, as Apple accepted them.</summary>
public sealed class DeviceCheckWriteResult
{
    /// <summary>The value set for the first bit, or null when the write left it out.</summary>
    [JsonPropertyName("bit0")]
    public bool? Bit0 { get; init; }

    /// <summary>The value set for the second bit, or null when the write left it out.</summary>
    [JsonPropertyName("bit1")]
    public bool? Bit1 { get; init; }
}

/// <summary>
/// The <see cref="FingerlyApiException.Code"/> values the DeviceCheck calls answer.
/// </summary>
public static class DeviceCheckErrors
{
    /// <summary><c>404</c>: no token is held for that call, or its hour has passed. Decide without the bits.</summary>
    public const string NotHeld = "device_check_not_held";

    /// <summary>
    /// <c>409</c>: the call's token was already used, a read's by a read or a
    /// write's by a write, or a retry was not the same request as the first
    /// attempt. Use another identification.
    /// </summary>
    public const string Spent = "device_check_spent";

    /// <summary><c>409</c>: another call is using the token right now.</summary>
    public const string Busy = "device_check_busy";

    /// <summary><c>409</c>: the project's Apple key is missing, refused by Apple, or has DeviceCheck off. The token is kept.</summary>
    public const string CredentialUnavailable = "credential_unavailable";

    /// <summary><c>422</c>: a write named neither bit.</summary>
    public const string BitsRequired = "bits_required";

    /// <summary>
    /// <c>422</c>: Apple rejected the token, which is used up.
    /// <see cref="FingerlyApiException.AppleStatus"/> holds Apple's status.
    /// </summary>
    public const string AppleRejected = "apple_rejected";

    /// <summary><c>503</c>: Apple or Fingerly's storage could not answer. The token is kept for a retry of the same request.</summary>
    public const string Unavailable = "device_check_unavailable";
}
