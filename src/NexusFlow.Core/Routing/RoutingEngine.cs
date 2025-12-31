using NexusFlow.Protocol.Control;

namespace NexusFlow.Core.Routing;

public sealed class RoutingEngine : IRoutingEngine
{
	private readonly string _localPeerId;
	private readonly IControlBroadcaster _control;

	private readonly object _gate = new();

	private long _lamport;

	private string _activeTarget;
	private LamportStamp _targetStamp;

	private string _activeSource;
	private LamportStamp _sourceStamp;

	public RoutingEngine(string localPeerId, IControlBroadcaster control)
	{
		_localPeerId = localPeerId;
		_control = control;

		_lamport = 0;

		_activeTarget = localPeerId;
		_activeSource = localPeerId;

		// Initial stamps are "0@me"
		_targetStamp = new LamportStamp(0, localPeerId);
		_sourceStamp = new LamportStamp(0, localPeerId);
	}

	public string ActiveTargetPeerId { get { lock (_gate) return _activeTarget; } }
	public string ActiveSourcePeerId { get { lock (_gate) return _activeSource; } }

	public event EventHandler<string>? ActiveTargetChanged;
	public event EventHandler<string>? ActiveSourceChanged;

	public (string ActiveTargetPeerId, LamportStamp TargetStamp,
			string ActiveSourcePeerId, LamportStamp SourceStamp) GetSnapshotV2()
	{
		lock (_gate)
			return (_activeTarget, _targetStamp, _activeSource, _sourceStamp);
	}

	public async Task RequestSetActiveTargetAsync(string targetPeerId, CancellationToken ct = default)
	{
		LamportStamp newStamp;
		bool changed;

		lock (_gate)
		{
			_lamport++;
			newStamp = new LamportStamp(_lamport, _localPeerId);

			// Always allow updating to new stamp even if same value (rarely useful but deterministic)
			changed = _activeTarget != targetPeerId || newStamp.IsNewerThan(_targetStamp);

			_activeTarget = targetPeerId;
			_targetStamp = newStamp;
		}

		if (changed)
			ActiveTargetChanged?.Invoke(this, targetPeerId);

		await _control.BroadcastAsync(new SetActiveTargetV2(targetPeerId, newStamp), ct).ConfigureAwait(false);
	}

	public async Task RequestSetActiveSourceAsync(string sourcePeerId, CancellationToken ct = default)
	{
		LamportStamp newStamp;
		bool changed;

		lock (_gate)
		{
			_lamport++;
			newStamp = new LamportStamp(_lamport, _localPeerId);

			changed = _activeSource != sourcePeerId || newStamp.IsNewerThan(_sourceStamp);

			_activeSource = sourcePeerId;
			_sourceStamp = newStamp;
		}

		if (changed)
			ActiveSourceChanged?.Invoke(this, sourcePeerId);

		await _control.BroadcastAsync(new SetActiveSourceV2(sourcePeerId, newStamp), ct).ConfigureAwait(false);
	}

	public void ApplyRemoteV2(object msg)
	{
		string? raiseTarget = null;
		string? raiseSource = null;

		lock (_gate)
		{
			switch (msg)
			{
				case SetActiveTargetV2 t:
					BumpLamportFromRemote(t.Stamp);
					if (t.Stamp.IsNewerThan(_targetStamp))
					{
						_activeTarget = t.TargetPeerId;
						_targetStamp = t.Stamp;
						raiseTarget = _activeTarget;
					}
					break;

				case SetActiveSourceV2 s:
					BumpLamportFromRemote(s.Stamp);
					if (s.Stamp.IsNewerThan(_sourceStamp))
					{
						_activeSource = s.SourcePeerId;
						_sourceStamp = s.Stamp;
						raiseSource = _activeSource;
					}
					break;

				case RoutingStateSyncV2 sync:
					BumpLamportFromRemote(sync.TargetStamp);
					BumpLamportFromRemote(sync.SourceStamp);

					if (sync.TargetStamp.IsNewerThan(_targetStamp))
					{
						_activeTarget = sync.ActiveTargetPeerId;
						_targetStamp = sync.TargetStamp;
						raiseTarget = _activeTarget;
					}

					if (sync.SourceStamp.IsNewerThan(_sourceStamp))
					{
						_activeSource = sync.ActiveSourcePeerId;
						_sourceStamp = sync.SourceStamp;
						raiseSource = _activeSource;
					}
					break;
			}
		}

		if (raiseTarget is not null) ActiveTargetChanged?.Invoke(this, raiseTarget);
		if (raiseSource is not null) ActiveSourceChanged?.Invoke(this, raiseSource);
	}

	private void BumpLamportFromRemote(in LamportStamp remote)
	{
		// Lamport: local = max(local, remote.counter) + 1 (on receive)
		_lamport = Math.Max(_lamport, remote.Counter) + 1;
	}

	public async Task HandlePeerDisconnectedAsync(string peerId, CancellationToken ct = default)
	{
		// If current active target/source points to the disconnected peer, fail-safe back to self.
		bool targetWasPeer, sourceWasPeer;

		lock (_gate)
		{
			targetWasPeer = string.Equals(_activeTarget, peerId, StringComparison.Ordinal);
			sourceWasPeer = string.Equals(_activeSource, peerId, StringComparison.Ordinal);
		}

		if (targetWasPeer)
			await RequestSetActiveTargetAsync(_localPeerId, ct).ConfigureAwait(false);

		if (sourceWasPeer)
			await RequestSetActiveSourceAsync(_localPeerId, ct).ConfigureAwait(false);
	}
}
