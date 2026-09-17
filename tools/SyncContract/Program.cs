// Refreshes the vendored server contract snapshot.
//
// The snapshot keeps the test suite independent of sibling repositories. Point
// FINGERLY_CONTRACT_DIR at the integration workspace's contract directory to
// pull a newer one:
//
//     FINGERLY_CONTRACT_DIR=../../contract dotnet run --project tools/SyncContract
//
// ContractTest verifies the result, so there is no separate check mode:
// "dotnet test" is the check.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

string[] files = ["openapi.json", "fixtures.json"];

string source = Environment.GetEnvironmentVariable("FINGERLY_CONTRACT_DIR") ?? "contract";
string target = Path.Combine(RepositoryRoot(), "contract");

byte[] manifest = File.ReadAllBytes(Path.Combine(source, "manifest.json"));
using JsonDocument parsed = JsonDocument.Parse(manifest);
JsonElement hashes = parsed.RootElement.GetProperty("files");

// Copy only after every file matches the hash the server recorded, so a
// half-written snapshot cannot land.
var staged = new Dictionary<string, byte[]> { ["manifest.json"] = manifest };
foreach (string name in files)
{
    byte[] content = File.ReadAllBytes(Path.Combine(source, name));
    string actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    string expected = hashes.GetProperty(name).GetString()
        ?? throw new InvalidOperationException($"the manifest records no hash for {name}");
    if (actual != expected)
    {
        throw new InvalidOperationException($"Invalid contract hash for {name}: {actual}");
    }
    staged[name] = content;
}

Directory.CreateDirectory(target);
foreach (KeyValuePair<string, byte[]> file in staged)
{
    File.WriteAllBytes(Path.Combine(target, file.Key), file.Value);
}
Console.WriteLine($".NET contract snapshot refreshed into {target}.");

// The repository root is wherever the solution is, so the tool writes to the
// same place whichever directory it was launched from.
static string RepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fingerly.sln")))
    {
        directory = directory.Parent;
    }
    return directory?.FullName
        ?? throw new InvalidOperationException("Fingerly.sln is not in any parent directory");
}
