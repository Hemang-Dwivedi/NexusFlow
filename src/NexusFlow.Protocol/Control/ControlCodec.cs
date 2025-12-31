using System.Text.Json;

namespace NexusFlow.Protocol.Control;

public static class ControlCodec
{
	private static readonly JsonSerializerOptions Opts = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false
	};

	/// <summary>
	/// Encode using the *runtime type* of the message so envelopes contain the correct Type.
	/// </summary>
	public static byte[] Encode(object msg)
	{
		if (msg is null) throw new ArgumentNullException(nameof(msg));

		var runtimeType = msg.GetType();
		var typeName = runtimeType.Name;

		using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(msg, runtimeType, Opts));

		var env = new Dictionary<string, object?>
		{
			["type"] = typeName,
			["body"] = doc.RootElement
		};

		return JsonSerializer.SerializeToUtf8Bytes(env, Opts);
	}

	/// <summary>
	/// Generic convenience overload (still uses runtime type correctly).
	/// </summary>
	public static byte[] Encode<T>(T msg) => Encode((object)msg!);

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
