using System.Net.Sockets;
using NexusFlow.Core.Diagnostics;
using NexusFlow.Protocol.Input;
using NexusFlow.Protocol.Transport;
using NexusFlow.Transport;

namespace NexusFlow.Core.InputTransport;

public sealed class InputReceiver
{
	private const string Cat = "input-remote";
	private readonly IDiagnosticsLog _log;

	public InputReceiver(IDiagnosticsLog log) => _log = log;

	public async Task HandleFirstFrameAsync(TcpClient client, NetworkStream stream, byte[] firstPayload, CancellationToken ct)
	{
		InputHelloV1 hello;
		try
		{
			hello = InputCodec.Decode<InputHelloV1>(firstPayload);
		}
		catch
		{
			client.Close();
			return;
		}

		// Phase F.2: receive-only. Still enforce "trusted only" later.
		// For now: just log who connected.
		_log.Info(Cat, $"RX input channel opened from {hello.FromPeerId}");

		try
		{
			while (!ct.IsCancellationRequested)
			{
				var (type, payload) = await FramingV2.ReadAsync(stream, ct).ConfigureAwait(false);
				if (type != MessageType.Input)
					continue; // or close

				var ev = InputCodec.Decode<InputEventV1>(payload);

				_log.Trace(Cat, $"RX {ev.FromPeerId} seq={ev.Seq} kind={ev.Kind}");
			}
		}
		catch
		{
			// drop
		}
		finally
		{
			try { client.Close(); } catch { }
			_log.Info(Cat, $"RX input channel closed from {hello.FromPeerId}");
		}
	}
}
