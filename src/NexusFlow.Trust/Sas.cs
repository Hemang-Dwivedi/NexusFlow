using System.Security.Cryptography;
using System.Text;

namespace NexusFlow.Trust;

public static class Sas
{
	public static string Compute6DigitCode(byte[] sharedSecret, byte[] transcript)
	{
		// Derive a stable HMAC from the shared secret
		using var hmac = new HMACSHA256(sharedSecret);
		var hash = hmac.ComputeHash(transcript);

		// Convert first 4 bytes to uint for uniform-ish distribution
		var value = BitConverter.ToUInt32(hash, 0);
		var code = (value % 1_000_000).ToString("D6");
		return code;
	}

	public static string Fingerprint(byte[] sharedSecret, byte[] transcript)
	{
		using var hmac = new HMACSHA256(sharedSecret);
		var hash = hmac.ComputeHash(transcript);
		return Convert.ToHexString(hash); // store full fingerprint
	}
}
