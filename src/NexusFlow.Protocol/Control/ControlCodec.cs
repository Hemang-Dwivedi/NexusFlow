using System.Text.Json;

namespace NexusFlow.Protocol.Control;

public static class ControlCodec
{
	private static readonly JsonSerializerOptions Opts = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false
	};

	// Wrap with a tiny envelope for type peek
	private sealed record Envelope(string Type, JsonElement Body);

	public static byte[] Encode<T>(T msg)
	{
		var type = typeof(T).Name;
		using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(msg, Opts));
		var env = new Dictionary<string, object?>
		{
			["type"] = type,
			["body"] = doc.RootElement
		};
		return JsonSerializer.SerializeToUtf8Bytes(env, Opts);
	}

	public static T? Decode<T>(byte[] payload)
	{
		using var doc = JsonDocument.Parse(payload);
		var body = doc.RootElement.GetProperty("body").GetRawText();
		return JsonSerializer.Deserialize<T>(body, Opts);
	}

	public static string PeekType(byte[] payload)
	{
		using var doc = JsonDocument.Parse(payload);
		return doc.RootElement.GetProperty("type").GetString() ?? "";
	}
}
