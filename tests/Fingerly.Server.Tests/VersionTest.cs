using System.Reflection;
using Xunit;

namespace Fingerly.Tests;

/// <summary>What the SDK reports upstream must be the version it is packed as.</summary>
public class VersionTest
{
    [Fact]
    public void ReportsThePackedVersion()
    {
        string informational = typeof(FingerlyClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        string packed = informational.Split('+')[0];

        Assert.Equal(FingerlyClient.Version, packed);
    }
}
