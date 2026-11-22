using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Fingerly.Tests;

// Pins every named constant to the exact string the server sends. The expected
// lists are copied from the server's own definitions (the risk signal catalogue
// and its groups, the suspect level bands, the attestation report, the webhook
// event types). A constant added without a server value, or a server value
// missing here, fails.
public class NamedValuesTest
{
    private static readonly string[] ServerDetectors =
    {
        "high_activity", "tampering", "device_farm", "developer_tools", "geo_mismatch",
        "device_inconsistent", "tor", "proxy", "residential_proxy",
        "residential_proxy_low_confidence", "residential_proxy_medium_confidence",
        "residential_proxy_high_confidence", "vpn_public", "vpn_relay",
        "vpn_mobile", "vpn_os_mismatch", "vpn_low_confidence", "vpn_medium_confidence",
        "vpn_high_confidence", "ip_blocklist_email_spam", "ip_blocklist_attack_source",
        "ip_abuser", "datacenter_ip", "bogon_ip", "anycast_ip", "incognito", "bad_bot",
        "ai_spoofed", "automation", "fetch_metadata_missing", "web_ticket_missing", "web_ticket_invalid",
        "antidetect_browser", "virtual_machine", "fingerprint_suppressed", "identity_obfuscated", "privacy_settings",
        "embedded_web_view", "remote_control", "emulator", "root_apps", "bootloader_unlocked", "app_cloner",
        "jailbreak", "simulator", "frida", "mitm_attack", "active_call", "attestation_missing",
        "attestation_downgrade", "repeated_reinstall", "device_activity_high",
        "device_activity_very_high", "play_device_integrity_failed", "play_app_unrecognized",
        "attestation_failed", "report_incomplete", "probe_errors", "custom_root_ca",
        "secondary_user_profile",
    };

    private static readonly string[] ServerGroups =
    {
        "tor", "datacenter_proxy", "residential_proxy", "vpn", "ip_blocklist", "ip_reputation",
        "network_anomaly", "location_spoofing", "ios_simulator", "android_emulator",
        "virtual_machine", "jailbroken_device", "rooted_device", "bootloader_unlocked", "cloned_app", "device_farm",
        "mitm_attack", "frida_detected", "browser_tampering", "fingerprint_suppressed",
        "identity_obfuscated", "developer_tools", "remote_control", "bot", "high_activity", "incognito_mode",
        "privacy_settings", "embedded_web_view", "active_call", "attestation_missing",
        "attestation_failed", "device_inconsistent", "report_incomplete", "custom_root_ca",
        "secondary_user_profile",
    };

    private static Dictionary<string, string> Constants(Type type)
    {
        var constants = new Dictionary<string, string>();
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            Assert.True(
                field.IsPublic && field.IsLiteral && field.FieldType == typeof(string),
                type.Name + "." + field.Name + " is not a public string constant");
            constants[field.Name] = (string)field.GetRawConstantValue()!;
        }
        return constants;
    }

    // "residential_proxy_low_confidence" -> "ResidentialProxyLowConfidence",
    // "identification.completed" -> "IdentificationCompleted", "" -> "Ok".
    private static string PascalCase(string value) =>
        value.Length == 0
            ? "Ok"
            : string.Concat(value.Split('_', '.').Select(part =>
                char.ToUpper(part[0], CultureInfo.InvariantCulture) + part.Substring(1)));

    private static void AssertConstants(Type type, params string[] expected)
    {
        Dictionary<string, string> constants = Constants(type);
        Assert.Equal(expected.OrderBy(v => v, StringComparer.Ordinal), constants.Values.OrderBy(v => v, StringComparer.Ordinal));
        foreach (KeyValuePair<string, string> constant in constants)
        {
            Assert.Equal(PascalCase(constant.Value), constant.Key);
        }
    }

    [Fact]
    public void SignalsCoverEveryServerDetectorAndGroup()
    {
        string[] expected = ServerDetectors.Union(ServerGroups).ToArray();
        Assert.Equal(75, expected.Length);
        AssertConstants(typeof(Signals), expected);
    }

    [Fact]
    public void ClosedSetsMatchTheServer()
    {
        AssertConstants(typeof(SuspectLevels), "low", "medium", "high");
        AssertConstants(typeof(Confidences), "low", "medium", "high");
        AssertConstants(typeof(AttestationStates), "attested", "not_attested", "failed");
        AssertConstants(typeof(AttestationStatuses),
            "verified", "indeterminate", "invalid", "absent", "unconfigured");
        AssertConstants(typeof(AttestSkippedReasons), "not_offered", "unsupported",
            "challenge_unavailable", "key_error", "timeout", "backoff", "install_unavailable");
        AssertConstants(typeof(WebhookEventTypes), "identification.completed",
            "identification.refused", "visitor.suspect", "billing.status_changed",
            "usage.daily_settled", "webhook.test");
        AssertConstants(typeof(WebhookEnvironments), "live", "test");
        AssertConstants(typeof(EventStates), "pending", "enriched", "unavailable");
        AssertConstants(typeof(Environments), "production", "staging", "development");
        AssertConstants(typeof(Platforms), "web", "android", "ios");
        AssertConstants(typeof(AnonymityNetworks), "tor", "relay", "vpn", "proxy", "hosting");
        AssertConstants(typeof(DeviceKinds), "desktop", "mobile", "tablet", "server");
        AssertConstants(typeof(VisitorKinds), "human", "ai_bot", "automation", "search_crawler", "unknown");
        AssertConstants(typeof(AiPurposes), "training", "search", "user_fetch", "agent");
        AssertConstants(typeof(AiRuntimes), "provider_cloud", "hosted_browser", "user_device");
        AssertConstants(typeof(AiVerificationMethods), "signature", "ip_list", "reverse_dns", "none");
        AssertConstants(typeof(AiVerificationResults), "pass", "fail", "not_attempted");
    }

    [Fact]
    public void VerifyReasonsMatchEverySdk()
    {
        Dictionary<string, string> constants = Constants(typeof(VerifyReasons));
        Assert.Equal(
            new[] { "", "already_consumed", "not_found", "tag_mismatch", "too_old", "unidentifiable", "unscored" },
            constants.Values.Distinct().OrderBy(v => v, StringComparer.Ordinal));
        Assert.Equal(VerifyReasons.Ok, VerifyReasons.None);
    }

    [Fact]
    public void ExistingConstantsAreTheSameStrings()
    {
        Assert.Equal(Signals.AttestationFailed, EventTrigger.SignalAttestationFailed);
        Assert.Equal(Signals.BrowserTampering, EventTrigger.SignalBrowserTampering);
        Assert.Equal(Signals.CustomRootCa, EventTrigger.SignalCustomRootCa);
        Assert.Equal(Signals.SecondaryUserProfile, EventTrigger.SignalSecondaryUserProfile);
    }

    // Constants rather than a wrapper type, so they work as switch labels.
    private static string Route(string? level) => level switch
    {
        SuspectLevels.High => "review",
        SuspectLevels.Medium or SuspectLevels.Low => "allow",
        _ => "unknown",
    };

    [Fact]
    public async Task UnknownServerValuesStillDeserialize()
    {
        await using TestApi api = await TestApi.RespondAsync(
            200,
            """
            {"request_id":"r1","visitor_id":"v1","occurred_at":"2026-01-02T03:04:05Z",
             "state":"enriched","suspect_score":90,"suspect_level":"severe","anonymity_network":"mesh",
             "triggers":[{"signal":"a_signal_from_the_future","weight":9},
                         {"signal":"fingerprint_suppressed","weight":4,"confidence":"certain"}]}
            """);
        using FingerlyClient fingerly = new(new FingerlyOptions
        {
            SecretKey = "fly_sk_staging_abc123",
            Endpoint = api.Origin,
            AllowInsecureLoopback = true,
        });

        Event stored = await fingerly.Events.GetAsync("r1");

        Assert.Equal("severe", stored.SuspectLevel);
        Assert.Equal("unknown", Route(stored.SuspectLevel));
        Assert.Equal("mesh", stored.AnonymityNetwork);
        Assert.Equal(EventStates.Enriched, stored.State);
        Assert.Equal("a_signal_from_the_future", stored.Triggers[0].Signal);
        Assert.Equal("certain", stored.Triggers[1].Confidence);
        Assert.Contains(stored.Triggers, t => t.Signal == Signals.FingerprintSuppressed);
        Assert.NotEqual(SuspectLevels.High, stored.SuspectLevel);
    }

    [Fact]
    public void UnknownWebhookTypeStillParses()
    {
        WebhookEvent delivery = Webhooks.Parse(Encoding.UTF8.GetBytes(
            """{"id":"evt_1","type":"something.new","environment":"live","data":{}}"""));

        Assert.Equal("something.new", delivery.Type);
        Assert.Equal(WebhookEnvironments.Live, delivery.Environment);
        Assert.NotEqual(WebhookEventTypes.VisitorSuspect, delivery.Type);
    }
}
