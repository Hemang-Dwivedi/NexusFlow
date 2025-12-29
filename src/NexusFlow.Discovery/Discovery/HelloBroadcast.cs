using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusFlow.Protocol.Discovery;

public static class DiscoveryProtocol
{
	public const int ProtocolVersion = 1;

	// Pick a port and keep it stable. Make configurable later via Settings.
	public const int UdpPort = 49721;

	// Simple magic prefix to quickly discard unrelated UDP noise.
	public const string Magic = "NEXUSFLOW_DISCOVERY_V1:";
}

public sealed record HelloBroadcast(
	string PeerId,
	string DeviceName,
	int TcpPort,
	int ProtocolVersion,
	long SentAtUnixMs
);

public static class HelloCodec
{
	private static readonly JsonSerializerOptions JsonOpts = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public static byte[] Encode(HelloBroadcast hello)
	{
		var json = JsonSerializer.Serialize(hello, JsonOpts);
		var payload = DiscoveryProtocol.Magic + json;
		return Encoding.UTF8.GetBytes(payload);
	}

	public static bool TryDecode(ReadOnlySpan<byte> data, out HelloBroadcast? hello)
	{
		hello = null;

		// Fast path: must be UTF8 + magic prefix
		var text = Encoding.UTF8.GetString(data);
		if (!text.StartsWith(DiscoveryProtocol.Magic, StringComparison.Ordinal))
			return false;

		var json = text.Substring(DiscoveryProtocol.Magic.Length);
		try
		{
			hello = JsonSerializer.Deserialize<HelloBroadcast>(json, JsonOpts);
			return hello is not null && hello.ProtocolVersion == DiscoveryProtocol.ProtocolVersion;
		}
		catch
		{
			return false;
		}
	}
}
