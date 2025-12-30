using System.Security.Cryptography;
using System.Text;

namespace NexusFlow.Trust;

public static class TrustKeys
{
	public static byte[] KeyFromFingerprintHex(string hexFingerprint)
	{
		// Fingerprint is hex string (64 bytes hex for HMACSHA256 output)
		var bytes = Convert.FromHexString(hexFingerprint);
		return SHA256.HashData(bytes);
	}

	public static byte[] ComputeMac(byte[] key, byte[] transcript)
	{
		using var hmac = new HMACSHA256(key);
		return hmac.ComputeHash(transcript);
	}
}
