using System.Security.Cryptography;
using System.Text;

namespace AutoSourcarr.Core.Repair.Runner.Protocol;

/// <summary>Identity minted only by a qualified conversation. It grants no liveness, build or repair-success claim.</summary>
public sealed class GuestArtifactSession
{
    private readonly byte[] _nonce;
    internal GuestArtifactSession(string jobId, string nonce)
    {
        if (jobId.Length != 32 || jobId.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            nonce.Length != 64 || nonce.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid private artifact session identity");
        JobId = jobId; _nonce = Encoding.ASCII.GetBytes(nonce); Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(jobId + "\n" + nonce)));
    }
    public string JobId { get; }
    public string Fingerprint { get; }
    public bool Matches(GuestArtifactBinding binding) => binding.JobId == JobId && binding.Session.Length == 64 &&
        binding.Session.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(binding.Session), _nonce);
}
