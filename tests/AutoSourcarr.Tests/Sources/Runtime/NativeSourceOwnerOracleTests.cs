using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Sources.Runtime;

namespace AutoSourcarr.Tests;

public static class NativeSourceOwnerOracleTests
{
    public static void Register(TestSuite suite) => suite.Add(
        "native owner oracle: managed codec matches actual C vectors and100 text-parser outcomes", () =>
        {
            var bytes = File.ReadAllBytes(ConfigurationTests.Fixture("native-source-owner.json"));
            Check.Equal("0392d649b8588b90b4204b4370546065fcfb2d239b3cac574a4406f126aa10b5", Hash(bytes));
            var corpus = JsonNode.Parse(bytes)!.AsObject();
            // Preserve the original oracle and its original source baseline.
            // Independent actual C executions after logging and exchange-root
            // ownership each retain all original vectors and parser outcomes.
            // Keep both historical source baselines, rather than repinning them.
            JsonObject current = corpus;
            foreach (var (filename, hash) in new[]
            {
                ("native-source-owner-current.json", "ea44f5ab1123ba71aa76118f013585cf456593aeda9e32b3ee3d4185dd1e7657"),
                ("native-source-owner-observation-root.json", "d72e4692812f1211fa104278477bdb8722e386e9ebd26c76e0a69f0184124d9b"),
                ("native-source-owner-snapshots.json", "c27d593ece3cb235d8c80d0b982223421fcf275ca802d878ea64c9f8703074c2")
            })
            {
                var currentBytes = File.ReadAllBytes(ConfigurationTests.Fixture(filename));
                Check.Equal(hash, Hash(currentBytes)); current = JsonNode.Parse(currentBytes)!.AsObject();
                Check.JsonEqual(System.Text.Json.JsonSerializer.SerializeToElement(corpus["vectors"]), System.Text.Json.JsonSerializer.SerializeToElement(current["vectors"]));
                Check.JsonEqual(System.Text.Json.JsonSerializer.SerializeToElement(corpus["environment"]), System.Text.Json.JsonSerializer.SerializeToElement(current["environment"]));
            }
            foreach (var name in new[] { "source-owner.c", "source-owner.h" })
                Check.Equal(current["source_hashes"]![name]!.GetValue<string>(),
                    Hash(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "NativeInputs", name))));
            var vectors = corpus["vectors"]!.AsObject();
            var nonce = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
            Check.True(Vector("StatusHex").SequenceEqual(NativeSourceOwnerProtocol.EncodeCommand(nonce, 1, NativeSourceOwnerOpcode.Status)));
            KeyValuePair<string, string>[] environment = [new("LANG", "C.UTF-8"), new("TOKEN", "x=é"), new("BIND_IP", "127.0.0.1")];
            Check.True(Vector("StartHex").SequenceEqual(NativeSourceOwnerProtocol.EncodeCommand(nonce, 2, NativeSourceOwnerOpcode.Start, environment)));
            var root = new NativeSourceOwnerRoot(0x0102030405060708UL, 0xfedcba9876543210UL);
            var expected = new NativeSourceOwnerProofMessage(0x01020304, 0x11223344, 568, 1000, root, NativeSourceOwnerProfile.Production);
            Check.Equal(expected, NativeSourceOwnerProtocol.DecodeProof(Vector("ProofHex"), nonce, expected));
            var response = NativeSourceOwnerProtocol.DecodeResponse(Vector("ResponseHex"), nonce,
                0x8877665544332211UL, NativeSourceOwnerOpcode.Stop, root);
            Check.Equal(0xfedcba9876543210UL, response.Generation);
            Check.Equal(-9, response.ExitStatus);
            Check.Equal((NativeSourceOwnerFacts)39, response.Facts);

            var compared = 0; var excludedAsset = 0;
            foreach (var row in corpus["environment"]!.AsArray())
            {
                if (!row!["asset_precondition"]!.GetValue<bool>())
                {
                    // Native trusted_asset was intentionally absent in this one
                    // controlled case. A text codec cannot attest an image asset.
                    Check.Equal("agent-text-valid-asset-refused", row["name"]!.GetValue<string>());
                    Check.True(!row["native_accepted"]!.GetValue<bool>()); excludedAsset++; continue;
                }
                var payload = Convert.FromHexString(row["payload_hex"]!.GetValue<string>());
                var frame = new byte[56 + payload.Length];
                Vector("StartHex").AsSpan(0, 56).CopyTo(frame);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(52), (uint)payload.Length);
                payload.CopyTo(frame.AsSpan(56));
                try
                {
                    if (row["native_accepted"]!.GetValue<bool>())
                        _ = NativeSourceOwnerProtocol.DecodeCommand(frame, nonce, 2);
                    else Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(frame, nonce, 2));
                }
                catch (Exception error) { throw new InvalidOperationException(row["name"]!.GetValue<string>(), error); }
                compared++;
            }
            Check.Equal(100, compared); Check.Equal(1, excludedAsset);
            byte[] Vector(string name) => Convert.FromHexString(vectors[name]!.GetValue<string>());
        });

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
