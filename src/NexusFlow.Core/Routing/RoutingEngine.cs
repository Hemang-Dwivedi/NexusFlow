using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Services;
using NexusFlow.Protocol.Control;

namespace NexusFlow.Core.Routing;

public sealed class RoutingEngine : IRoutingEngine
{
	private const string Cat = "routing";

	private readonly string _localPeerId;
	private readonly IControlBroadcaster _control;
	private readonly IFailsafeService _failsafe;
	private readonly IDiagnosticsLog _log;

	private readonly object _gate = new();
	private long _lamport;

	private string _activeTarget;
	private LamportStamp _targetStamp;

	private string _activeSource;
	private LamportStamp _sourceStamp;

	public RoutingEngine(string localPeerId, IControlBroadcaster control, IFailsafeService failsafe, IDiagnosticsLog log)
	{
		_localPeerId = localPeerId;
		_control = control;
		_failsafe = failsafe;
		_log = log;

		_lamport = 0;

		_activeTarget = localPeerId;
		_activeSource = localPeerId;

		_targetStamp = new LamportStamp(0, localPeerId);
		_sourceStamp = new LamportStamp(0, localPeerId);

		_failsafe.Changed += OnFailsafeChanged;
	}

	public string ActiveTargetPeerId
	{
		get
		{
			if (_failsafe.IsBlocked) return _localPeerId;
			lock (_gate) return _activeTarget;
		}
	}

	public string ActiveSourcePeerId
	{
		get
		{
			if (_failsafe.IsBlocked) return _localPeerId;
			lock (_gate) return _activeSource;
		}
	}

	public bool isFailsafeActive => _failsafe.IsBlocked;

	public event EventHandler<string>? ActiveTargetChanged;
	public event EventHandler<string>? ActiveSourceChanged;
	public event Action<EntryEdge, double>? CursorWarpRequested;

	public (string ActiveTargetPeerId, LamportStamp TargetStamp,
			string ActiveSourcePeerId, LamportStamp SourceStamp) GetSnapshotV2()
	{
		lock (_gate)
			return (_activeTarget, _targetStamp, _activeSource, _sourceStamp);
	}

	
	public RoutingApplyResult TryApplyRemoteV2(object msg)
	{
		if (_failsafe.IsBlocked)
			return new RoutingApplyResult(RoutingApplyDecision.Ignored_FailsafeBlocked);

		string? raiseTarget = null;
		string? raiseSource = null;
		(EntryEdge Edge, double Normalized)? warpArgs = null;

		lock (_gate)
		{
			switch (msg)
			{
				case SetActiveTargetV2 t:
					BumpLamportFromRemote(t.Stamp);
					if (!t.Stamp.IsNewerThan(_targetStamp))
						return new RoutingApplyResult(RoutingApplyDecision.Ignored_OlderStamp,
							$"target old {t.Stamp.Counter}@{t.Stamp.PeerId} <= {_targetStamp.Counter}@{_targetStamp.PeerId}");

					_activeTarget = t.TargetPeerId;
					_targetStamp = t.Stamp;
					raiseTarget = _activeTarget;

					// If we are being made the target and the sender included entry info, warp cursor.
					if (t.EntryEdge != EntryEdge.None &&
					    string.Equals(t.TargetPeerId, _localPeerId, StringComparison.Ordinal))
						warpArgs = (t.EntryEdge, t.EntryNormalized);
					break;

				case SetActiveSourceV2 s:
					BumpLamportFromRemote(s.Stamp);
					if (!s.Stamp.IsNewerThan(_sourceStamp))
						return new RoutingApplyResult(RoutingApplyDecision.Ignored_OlderStamp,
							$"source old {s.Stamp.Counter}@{s.Stamp.PeerId} <= {_sourceStamp.Counter}@{_sourceStamp.PeerId}");

					_activeSource = s.SourcePeerId;
					_sourceStamp = s.Stamp;
					raiseSource = _activeSource;
					break;

				case RoutingStateSyncV2 sync:
					BumpLamportFromRemote(sync.TargetStamp);
					BumpLamportFromRemote(sync.SourceStamp);

					var appliedAny = false;

					if (sync.TargetStamp.IsNewerThan(_targetStamp))
					{
						_activeTarget = sync.ActiveTargetPeerId;
						_targetStamp = sync.TargetStamp;
						raiseTarget = _activeTarget;
						appliedAny = true;
					}

					if (sync.SourceStamp.IsNewerThan(_sourceStamp))
					{
						_activeSource = sync.ActiveSourcePeerId;
						_sourceStamp = sync.SourceStamp;
						raiseSource = _activeSource;
						appliedAny = true;
					}

					if (!appliedAny)
						return new RoutingApplyResult(RoutingApplyDecision.Ignored_OlderStamp, "sync not newer");
					break;

				default:
					return new RoutingApplyResult(RoutingApplyDecision.Ignored_UnknownMessage, msg.GetType().Name);
			}
		}

		if (raiseTarget is not null) ActiveTargetChanged?.Invoke(this, ActiveTargetPeerId);
		if (raiseSource is not null) ActiveSourceChanged?.Invoke(this, ActiveSourcePeerId);
		if (warpArgs.HasValue) CursorWarpRequested?.Invoke(warpArgs.Value.Edge, warpArgs.Value.Normalized);

		return new RoutingApplyResult(RoutingApplyDecision.Applied);
	}

	private void BumpLamportFromRemote(in LamportStamp remote)
		=> _lamport = Math.Max(_lamport, remote.Counter) + 1;

	public async Task HandlePeerDisconnectedAsync(string peerId, CancellationToken ct = default)
	{
		// If active values point to disconnected peer, revert to self (normal behavior).
		var needTarget = string.Equals(ActiveTargetPeerId, peerId, StringComparison.Ordinal);
		var needSource = string.Equals(ActiveSourcePeerId, peerId, StringComparison.Ordinal);

		if (needTarget) await RequestSetActiveTargetAsync(_localPeerId, ct).ConfigureAwait(false);
		if (needSource) await RequestSetActiveSourceAsync(_localPeerId, ct).ConfigureAwait(false);
	}

	private void OnFailsafeChanged(bool blocked)
	{
		if (blocked)
		{
			_log.Warn(Cat, "FAILSAFE ON: forcing local control (target/source = self). Remote updates will be ignored.");
			ActiveTargetChanged?.Invoke(this, _localPeerId);
			ActiveSourceChanged?.Invoke(this, _localPeerId);
		}
		else
		{
			_log.Info(Cat, "FAILSAFE OFF: routing resumes (remote updates accepted).");
			ActiveTargetChanged?.Invoke(this, ActiveTargetPeerId);
			ActiveSourceChanged?.Invoke(this, ActiveSourcePeerId);
		}
	}

	// Add inside RoutingEngine (same file)

	public Task SetActiveTargetLocalOnlyAsync(string targetPeerId, CancellationToken ct = default)
		=> SetActiveTargetCoreAsync(targetPeerId, EntryEdge.None, 0.5, broadcast: false, ct);

	public Task SetActiveSourceLocalOnlyAsync(string sourcePeerId, CancellationToken ct = default)
		=> SetActiveSourceCoreAsync(sourcePeerId, broadcast: false, ct);

	// Keep your existing methods, but forward them:
	public Task RequestSetActiveTargetAsync(string targetPeerId, CancellationToken ct = default)
		=> SetActiveTargetCoreAsync(targetPeerId, EntryEdge.None, 0.5, broadcast: true, ct);

	public Task RequestSetActiveTargetAsync(string targetPeerId, EntryEdge entryEdge, double entryNormalized, CancellationToken ct = default)
		=> SetActiveTargetCoreAsync(targetPeerId, entryEdge, entryNormalized, broadcast: true, ct);

	public Task RequestSetActiveSourceAsync(string sourcePeerId, CancellationToken ct = default)
		=> SetActiveSourceCoreAsync(sourcePeerId, broadcast: true, ct);

	// New shared implementations (private)
	private async Task SetActiveTargetCoreAsync(string targetPeerId, EntryEdge entryEdge, double entryNormalized, bool broadcast, CancellationToken ct)
	{
		if (_failsafe.IsBlocked && !string.Equals(targetPeerId, _localPeerId, StringComparison.Ordinal))
		{
			_log.Warn(Cat, $"Failsafe blocked: ignoring local SetActiveTarget -> {targetPeerId}");
			return;
		}

		LamportStamp newStamp;
		lock (_gate)
		{
			_lamport++;
			newStamp = new LamportStamp(_lamport, _localPeerId);
			_activeTarget = targetPeerId;
			_targetStamp = newStamp;
		}

		ActiveTargetChanged?.Invoke(this, ActiveTargetPeerId);

		// local-only or failsafe => no broadcast
		if (!broadcast || _failsafe.IsBlocked)
		{
			_log.Info(Cat, $"Applied {(broadcast ? "failsafe-local" : "local-only")} SetActiveTarget -> {ActiveTargetPeerId}");
			return;
		}

		_log.Info(Cat, $"TX SetActiveTargetV2 -> {targetPeerId} entry={entryEdge}@{entryNormalized:F2} stamp={newStamp.Counter}@{newStamp.PeerId}");
		await _control.BroadcastAsync(new SetActiveTargetV2(targetPeerId, newStamp, entryEdge, entryNormalized), ct).ConfigureAwait(false);
	}

	private async Task SetActiveSourceCoreAsync(string sourcePeerId, bool broadcast, CancellationToken ct)
	{
		if (_failsafe.IsBlocked && !string.Equals(sourcePeerId, _localPeerId, StringComparison.Ordinal))
		{
			_log.Warn(Cat, $"Failsafe blocked: ignoring local SetActiveSource -> {sourcePeerId}");
			return;
		}

		LamportStamp newStamp;
		lock (_gate)
		{
			_lamport++;
			newStamp = new LamportStamp(_lamport, _localPeerId);
			_activeSource = sourcePeerId;
			_sourceStamp = newStamp;
		}

		ActiveSourceChanged?.Invoke(this, ActiveSourcePeerId);

		if (!broadcast || _failsafe.IsBlocked)
		{
			_log.Info(Cat, $"Applied {(broadcast ? "failsafe-local" : "local-only")} SetActiveSource -> {ActiveSourcePeerId}");
			return;
		}

		_log.Info(Cat, $"TX SetActiveSourceV2 -> {sourcePeerId} stamp={newStamp.Counter}@{newStamp.PeerId}");
		await _control.BroadcastAsync(new SetActiveSourceV2(sourcePeerId, newStamp), ct).ConfigureAwait(false);
	}

}
