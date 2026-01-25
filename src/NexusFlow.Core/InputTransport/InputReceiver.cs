using System.Net.Sockets;
using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Services;
using NexusFlow.Core.Routing;
using NexusFlow.Identity;
using NexusFlow.Protocol.Input;
using NexusFlow.Protocol.Transport;
using NexusFlow.Transport;
using NexusFlow.Trust;

namespace NexusFlow.Core.InputTransport;

public sealed class InputReceiver
{
	private const string Cat = "input-remote";

	private readonly IDiagnosticsLog _log;
	private readonly TrustStore _trust;
	private readonly IInputAuthKeyProvider _keys;

	private readonly ILocalIdentity _me;
	private readonly RoutingEngine _routing;
	private readonly IFailsafeService _failsafe;
	private readonly IRemoteInputSink _sink;

	public InputReceiver(
		IDiagnosticsLog log,
		TrustStore trust,
		IInputAuthKeyProvider keys,
		ILocalIdentity me,
		RoutingEngine routing,
		IFailsafeService failsafe,
		IRemoteInputSink sink)
	{
		_log = log;
		_trust = trust;
		_keys = keys;
		_me = me;
		_routing = routing;
		_failsafe = failsafe;
		_sink = sink;
	}

	public async Task HandleFirstFrameAsync(TcpClient client, NetworkStream stream, byte[] firstPayload, CancellationToken ct)
	{
		// ... keep your existing F.4/F.5 hello auth here ...

		// After successful auth:
		// hello.FromPeerId is authenticated + trusted
		var fromPeerId = /* hello.FromPeerId */ NexusFlow.Protocol.Input.InputCodec.Decode<InputHelloV2>(firstPayload).FromPeerId;

		_log.Info(Cat, $"RX input channel AUTHENTICATED from {fromPeerId}");

		var gate = new OrderedInputGate(maxBuffer: 512);

		try
		{
			while (!ct.IsCancellationRequested)
			{
				var (type, payload) = await FramingV2.ReadAsync(stream, ct).ConfigureAwait(false);
				if (type != MessageType.Input) continue;

				var ev = InputCodec.Decode<InputEventV1>(payload);

				// Hard safety: never accept remote input while failsafe is on
				if (_failsafe.IsBlocked)
					continue;

				// Only accept remote input if *this* peer is the active target
				if (!string.Equals(_routing.ActiveTargetPeerId, _me.PeerId, StringComparison.Ordinal))
					continue;

				// Optional but recommended: require active source matches sender
				// If your switching isn’t stable yet, you can comment this out for now.
				if (!string.Equals(_routing.ActiveSourcePeerId, fromPeerId, StringComparison.Ordinal))
					continue;

				foreach (var ready in gate.Offer(ev))
				{
					_sink.Apply(ready);
				}
			}
		}
		catch
		{
			// drop
		}
		finally
		{
			try { client.Close(); } catch { }
			_log.Info(Cat, $"RX input channel closed from {fromPeerId} nextSeq={gate.ExpectedNextSeq} buffered={gate.BufferedCount}");
		}
	}

	private bool IsTrustedPeer(string peerId)
	{
		var state = _trust.Load();
		return state.Peers.Any(p => p.PeerId == peerId);
	}
}
