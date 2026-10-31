using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Tests;

public static class CanonicalJsonTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("repair canonical JSON: released float, Unicode, ordering and hash bytes", () =>
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(ConfigurationTests.Fixture("canonical-json.json")));
            foreach (var row in fixture.RootElement.EnumerateArray())
            {
                var node = JsonNode.Parse(row.GetProperty("input_json").GetString()!);
                Check.Equal(row.GetProperty("canonical").GetString(), CanonicalJson.Text(node));
                Check.Equal(row.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Bytes(node))));
            }
            Console.WriteLine("Repair canonical JSON parity cases " + fixture.RootElement.GetArrayLength());
        });
    }
}
