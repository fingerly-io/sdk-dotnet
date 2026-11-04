using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Fingerly.Tests;

/// <summary>No SDK object may print or serialize a credential.</summary>
public class RedactionTest
{
    private const string SecretKey = "fly_sk_production_reader1234";
    private const string ProxyKey = "fly_px_production_proxy1234";
    private const string PublicKey = "fly_pk_production_browser1234";

    private static readonly string[] Credentials =
    {
        SecretKey, ProxyKey, PublicKey, "session=secret", "Bearer secret",
    };

    private static void AssertClean(string rendering)
    {
        foreach (string credential in Credentials)
        {
            Assert.DoesNotContain(credential, rendering, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ClientsProxiesOptionsAndRequestsPrintNoCredential()
    {
        var clientOptions = new FingerlyOptions { SecretKey = SecretKey };
        using var client = new FingerlyClient(clientOptions);
        var proxyOptions = new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Path = "/k7x2qm4a",
            ClientIp = _ => "203.0.113.5",
        };
        using var proxy = new FingerlyProxy(proxyOptions);
        var request = new ProxyRequest("POST", "/k7x2qm4a") { Body = new MemoryStream(Encoding.UTF8.GetBytes("{}")) };
        request.AddHeader("X-API-Key", PublicKey);
        request.AddHeader("Cookie", "session=secret");
        request.AddHeader("Authorization", "Bearer secret");

        foreach (object value in new object[] { clientOptions, client, client.Events, proxyOptions, proxy, request })
        {
            AssertClean(value.ToString()!);
        }
        Assert.Contains("[redacted]", client.ToString(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", proxy.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsSerializeWithoutTheirKeys()
    {
        var reflection = new JsonSerializerOptions
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };
        string client = JsonSerializer.Serialize(new FingerlyOptions { SecretKey = SecretKey }, reflection);
        string proxy = JsonSerializer.Serialize(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Path = "/k7x2qm4a",
            ClientIp = _ => "203.0.113.5",
        }, reflection);

        AssertClean(client);
        AssertClean(proxy);
        Assert.Contains("/k7x2qm4a", proxy, StringComparison.Ordinal);
    }
}
