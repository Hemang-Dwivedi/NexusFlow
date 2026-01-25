using System.Threading;
using NexusFlow.Core.Diagnostics;
using NexusFlow.Protocol.Input;

namespace NexusFlow.Core.InputTransport;

public sealed class DiagnosticsRemoteInputSink : IRemoteInputSink
{
	private const string Cat = "input-apply";
	private readonly IDiagnosticsLog _log;

	private long _applied;

	public DiagnosticsRemoteInputSink(IDiagnosticsLog log) => _log = log;

	public void Apply(InputEventV1 ev)
	{
		var n = Interlocked.Increment(ref _applied);
		if ((n % 50) == 0)
			_log.Info(Cat, $"Applied remote inputs: {n}");

		// Keep trace lightweight
		_log.Trace(Cat, $"APPLY {ev.FromPeerId} seq={ev.Seq} kind={ev.Kind}");
	}

	public long AppliedCount => Interlocked.Read(ref _applied);
}
