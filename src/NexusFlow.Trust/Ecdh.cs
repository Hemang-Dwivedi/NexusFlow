using System.Security.Cryptography;

namespace NexusFlow.Trust;

public static class Ecdh
{
	public static ECDiffieHellman Create() =>
		ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

	public static byte[] ExportPublic(ECDiffieHellman ecdh) =>
		ecdh.ExportSubjectPublicKeyInfo();

	public static ECDiffieHellmanPublicKey ImportPublic(byte[] spki)
	{
		var tmp = ECDiffieHellman.Create();
		tmp.ImportSubjectPublicKeyInfo(spki, out _);
		return tmp.PublicKey;
	}
}
