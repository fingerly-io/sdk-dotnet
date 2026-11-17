namespace Fingerly;

/// <summary>
/// The signal identifiers Fingerly reports, as the exact strings the API sends.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of identifier share this vocabulary. <see cref="EventTrigger.Signal"/>
/// on a stored event carries a signal group, such as <see cref="Vpn"/>, so one
/// VPN is one trigger however many detectors saw it. The archived document's
/// <c>risk.triggers[].signal</c> carries the detector itself, such as
/// <see cref="VpnPublic"/>. Many groups have a single detector of the same
/// name, such as <see cref="Tor"/> or <see cref="FingerprintSuppressed"/>.
/// </para>
/// <para>
/// The properties stay plain strings, so a signal the server adds after this
/// SDK was released reads normally. These are <see langword="const"/>, so they
/// work as <see langword="switch"/> labels; keep a default branch for values
/// not listed here.
/// </para>
/// <code>
/// bool suppressed = ev.Triggers.Any(t => t.Signal == Signals.FingerprintSuppressed);
/// </code>
/// </remarks>
public static class Signals
{
    /// <summary>Group and detector: a published Tor exit node.</summary>
    public const string Tor = "tor";

    /// <summary>Group: a declared datacenter proxy or an unexplained datacenter address.</summary>
    public const string DatacenterProxy = "datacenter_proxy";

    /// <summary>Detector: a datacenter proxy, an address that looks like a proxy and lives where servers do.</summary>
    public const string Proxy = "proxy";

    /// <summary>Detector: an address that lives where servers do, with no proxy or VPN claim attached.</summary>
    public const string DatacenterIp = "datacenter_ip";

    /// <summary>Group and detector: a proxy on a consumer connection.</summary>
    public const string ResidentialProxy = "residential_proxy";

    /// <summary>Detector: a residential proxy at low confidence, in confidence weighting mode.</summary>
    public const string ResidentialProxyLowConfidence = "residential_proxy_low_confidence";

    /// <summary>Detector: a residential proxy at medium confidence, in confidence weighting mode.</summary>
    public const string ResidentialProxyMediumConfidence = "residential_proxy_medium_confidence";

    /// <summary>Detector: a residential proxy at high confidence, in confidence weighting mode.</summary>
    public const string ResidentialProxyHighConfidence = "residential_proxy_high_confidence";

    /// <summary>Group: the traffic arrived over a VPN.</summary>
    public const string Vpn = "vpn";

    /// <summary>Detector: a commercial VPN endpoint.</summary>
    public const string VpnPublic = "vpn_public";

    /// <summary>Detector: an address whose country disagrees with the device's timezone.</summary>
    public const string VpnTimezone = "vpn_timezone";

    /// <summary>Detector: Apple's Private Relay and similar relays.</summary>
    public const string VpnRelay = "vpn_relay";

    /// <summary>Detector: a VPN on a mobile carrier network. Native only.</summary>
    public const string VpnMobile = "vpn_mobile";

    /// <summary>Detector: a claimed operating system the connection's own fingerprint disagrees with. Web only.</summary>
    public const string VpnOsMismatch = "vpn_os_mismatch";

    /// <summary>Detector: a VPN at low confidence, in confidence weighting mode.</summary>
    public const string VpnLowConfidence = "vpn_low_confidence";

    /// <summary>Detector: a VPN at medium confidence, in confidence weighting mode.</summary>
    public const string VpnMediumConfidence = "vpn_medium_confidence";

    /// <summary>Detector: a VPN at high confidence, in confidence weighting mode.</summary>
    public const string VpnHighConfidence = "vpn_high_confidence";

    /// <summary>Group: the address is on a public spam or attack-source blocklist.</summary>
    public const string IpBlocklist = "ip_blocklist";

    /// <summary>Detector: an address on a mail-abuse blocklist.</summary>
    public const string IpBlocklistEmailSpam = "ip_blocklist_email_spam";

    /// <summary>Detector: an address on an attack-source blocklist.</summary>
    public const string IpBlocklistAttackSource = "ip_blocklist_attack_source";

    /// <summary>Group: the address has a recent abuse history.</summary>
    public const string IpReputation = "ip_reputation";

    /// <summary>Detector: an address with a history of abuse, broader and weaker than either blocklist.</summary>
    public const string IpAbuser = "ip_abuser";

    /// <summary>Group: a bogon or anycast address. Both detectors ship disabled.</summary>
    public const string NetworkAnomaly = "network_anomaly";

    /// <summary>Detector: an address that cannot appear on the public internet.</summary>
    public const string BogonIp = "bogon_ip";

    /// <summary>Detector: an address announced from many places at once.</summary>
    public const string AnycastIp = "anycast_ip";

    /// <summary>Group: the device's own idea of where it is disagrees with its address.</summary>
    public const string LocationSpoofing = "location_spoofing";

    /// <summary>Detector: the device's timezone or locale disagrees with where its address is.</summary>
    public const string GeoMismatch = "geo_mismatch";

    /// <summary>Group: the app runs in the iOS simulator.</summary>
    public const string IosSimulator = "ios_simulator";

    /// <summary>Detector: the iOS simulator.</summary>
    public const string Simulator = "simulator";

    /// <summary>Group: the app runs in an Android emulator.</summary>
    public const string AndroidEmulator = "android_emulator";

    /// <summary>Detector: an Android emulator.</summary>
    public const string Emulator = "emulator";

    /// <summary>Group and detector: a browser running inside a virtual machine.</summary>
    public const string VirtualMachine = "virtual_machine";

    /// <summary>Group: a jailbroken iOS device.</summary>
    public const string JailbrokenDevice = "jailbroken_device";

    /// <summary>Detector: a jailbroken iOS device.</summary>
    public const string Jailbreak = "jailbreak";

    /// <summary>Group: a rooted Android device.</summary>
    public const string RootedDevice = "rooted_device";

    /// <summary>Detector: a rooted Android device.</summary>
    public const string RootApps = "root_apps";

    /// <summary>Detector: Play Integrity says the device meets no device, strong or virtual integrity.</summary>
    public const string PlayDeviceIntegrityFailed = "play_device_integrity_failed";

    /// <summary>Group: the app runs under a cloning framework or is not recognised by Google Play.</summary>
    public const string ClonedApp = "cloned_app";

    /// <summary>Detector: an app running under a cloning framework.</summary>
    public const string AppCloner = "app_cloner";

    /// <summary>Detector: an app Google Play does not recognise, or signed by a certificate the key does not allow.</summary>
    public const string PlayAppUnrecognized = "play_app_unrecognized";

    /// <summary>Group and detector: a device that looks mass-provisioned or reset-fresh.</summary>
    public const string DeviceFarm = "device_farm";

    /// <summary>Detector: an iOS device whose App Attest risk metric shows the app reinstalled or the device reset again and again.</summary>
    public const string RepeatedReinstall = "repeated_reinstall";

    /// <summary>Detector: Play Integrity recent device activity at <c>LEVEL_3</c>.</summary>
    public const string DeviceActivityHigh = "device_activity_high";

    /// <summary>Detector: Play Integrity recent device activity at <c>LEVEL_4</c>.</summary>
    public const string DeviceActivityVeryHigh = "device_activity_very_high";

    /// <summary>Group and detector: a proxy intercepting the app's own TLS.</summary>
    public const string MitmAttack = "mitm_attack";

    /// <summary>Group: an instrumentation framework is attached to the process.</summary>
    public const string FridaDetected = "frida_detected";

    /// <summary>Detector: an instrumentation framework attached to the process.</summary>
    public const string Frida = "frida";

    /// <summary>Group: the runtime was edited, claims an identity it does not have, or reused a freshness ticket.</summary>
    public const string BrowserTampering = "browser_tampering";

    /// <summary>Detector: runtime code or natives were patched or contradict what was observed.</summary>
    public const string Tampering = "tampering";

    /// <summary>Detector: a build made to defeat fingerprinting, whose observed identity contradicts its claim.</summary>
    public const string AntidetectBrowser = "antidetect_browser";

    /// <summary>Detector: a browser report whose freshness ticket did not verify: bad signature, another key's or origin's, expired, or already used.</summary>
    public const string WebTicketInvalid = "web_ticket_invalid";

    /// <summary>Group and detector: a report too thin to identify anyone.</summary>
    public const string FingerprintSuppressed = "fingerprint_suppressed";

    /// <summary>Group and detector: an open inspector or an attached debugger.</summary>
    public const string DeveloperTools = "developer_tools";

    /// <summary>Group and detector: a session driven through remote desktop software.</summary>
    public const string RemoteControl = "remote_control";

    /// <summary>Group: the client is not a person.</summary>
    public const string Bot = "bot";

    /// <summary>Detector: a browser driven by something other than a person.</summary>
    public const string Automation = "automation";

    /// <summary>Detector: a client that says it is not a browser, or a known bad crawler.</summary>
    public const string BadBot = "bad_bot";

    /// <summary>Detector: a browser report without <c>Sec-Fetch-*</c> headers from a browser that always sends them.</summary>
    public const string FetchMetadataMissing = "fetch_metadata_missing";

    /// <summary>Group and detector: a device seen far more often than an ordinary one.</summary>
    public const string HighActivity = "high_activity";

    /// <summary>Group: a private browsing window.</summary>
    public const string IncognitoMode = "incognito_mode";

    /// <summary>Detector: a private browsing window.</summary>
    public const string Incognito = "incognito";

    /// <summary>Group and detector: hardened privacy settings. Ships at weight zero.</summary>
    public const string PrivacySettings = "privacy_settings";

    /// <summary>Group and detector: a page running inside an app's web view. Ships at weight zero.</summary>
    public const string EmbeddedWebView = "embedded_web_view";

    /// <summary>Group and detector: a device on a voice call while the submission was made.</summary>
    public const string ActiveCall = "active_call";

    /// <summary>Group and detector: a native device that could have offered hardware attestation proof and did not.</summary>
    public const string AttestationMissing = "attestation_missing";

    /// <summary>Group and detector: hardware attestation failed cryptographic, chain, replay or ownership verification.</summary>
    public const string AttestationFailed = "attestation_failed";

    /// <summary>Detector: an unproven or rejected native report naming an install an attested key owns.</summary>
    public const string AttestationDowngrade = "attestation_downgrade";

    /// <summary>Group and detector: reported hardware, OS or runtime realms contradict each other.</summary>
    public const string DeviceInconsistent = "device_inconsistent";

    /// <summary>Group and detector: evidence a report always carries is missing.</summary>
    public const string ReportIncomplete = "report_incomplete";

    /// <summary>Detector: a native security source ran but lists probes that threw, were denied or timed out.</summary>
    public const string ProbeErrors = "probe_errors";

    /// <summary>Detector: a browser report that carried no single-use freshness ticket.</summary>
    public const string WebTicketMissing = "web_ticket_missing";

    /// <summary>Group and detector: a TLS chain anchored at a non-system or user-installed root authority.</summary>
    public const string CustomRootCa = "custom_root_ca";

    /// <summary>Group and detector: a managed Android work profile or secondary user profile without cloner indicators.</summary>
    public const string SecondaryUserProfile = "secondary_user_profile";
}
