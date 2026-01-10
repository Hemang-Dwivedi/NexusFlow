using System.Text.Json;

namespace NexusFlow.Protocol.Input;

public static class InputCodec
{
	private static readonly JsonSerializerOptions Opts = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false
	};

	public static byte[] Encode<T>(T msg) =>
		JsonSerializer.SerializeToUtf8Bytes(msg, Opts);

	public static T Decode<T>(byte[] payload) =>
		JsonSerializer.Deserialize<T>(payload, Opts)!;
}
