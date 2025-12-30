using System.Text.Json;

namespace NexusFlow.Protocol.Pairing;

public static class PairingCodec
{
	private static readonly JsonSerializerOptions Opts = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false
	};

	public static byte[] Encode<T>(T msg) => JsonSerializer.SerializeToUtf8Bytes(msg, Opts);
	public static T? Decode<T>(byte[] data) => JsonSerializer.Deserialize<T>(data, Opts);
}
