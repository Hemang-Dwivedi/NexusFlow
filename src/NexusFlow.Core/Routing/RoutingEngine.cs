using NexusFlow.Protocol.Control;

namespace NexusFlow.Core.Routing;

public sealed class RoutingEngine : IRoutingEngine
{
	private readonly string _localPeerId;
	private readonly IControlBroadcaster _control;

	private readonly object _gate = new();

	private string _activeTarget;
	private string _activeSource;

	public RoutingEngine(string localPeerId, IControlBroadcaster control)
	{
		_localPeerId = localPeerId;
		_control = control;

		_activeTarget = localPeerId;
		_activeSource = localPeerId;
	}

	public string ActiveTargetPeerId { get { lock (_gate) return _activeTarget; } }
	public string ActiveSourcePeerId { get { lock (_gate) return _activeSource; } }

	public event EventHandler<string>? ActiveTargetChanged;
	public event EventHandler<string>? ActiveSourceChanged;

	public (string ActiveTargetPeerId, string ActiveSourcePeerId) GetSnapshot()
		=> (ActiveTargetPeerId, ActiveSourcePeerId);

	public async Task RequestSetActiveTargetAsync(string targetPeerId, CancellationToken ct = default)
	{
		bool changed;
		lock (_gate)
		{
			changed = _activeTarget != targetPeerId;
			if (changed) _activeTarget = targetPeerId;
		}

		if (!changed) return;

		ActiveTargetChanged?.Invoke(this, targetPeerId);
		await _control.BroadcastAsync(new SetActiveTarget(targetPeerId), ct).ConfigureAwait(false);
	}

	public async Task RequestSetActiveSourceAsync(string sourcePeerId, CancellationToken ct = default)
	{
		bool changed;
		lock (_gate)
		{
			changed = _activeSource != sourcePeerId;
			if (changed) _activeSource = sourcePeerId;
		}

		if (!changed) return;

		ActiveSourceChanged?.Invoke(this, sourcePeerId);
		await _control.BroadcastAsync(new SetActiveSource(sourcePeerId), ct).ConfigureAwait(false);
	}

	public void ApplyRemote(object msg)
	{
		string? newTarget = null;
		string? newSource = null;

		lock (_gate)
		{
			switch (msg)
			{
				case SetActiveTarget t:
					if (_activeTarget != t.TargetPeerId)
					{
						_activeTarget = t.TargetPeerId;
						newTarget = _activeTarget;
					}
					break;

				case SetActiveSource s:
					if (_activeSource != s.SourcePeerId)
					{
						_activeSource = s.SourcePeerId;
						newSource = _activeSource;
					}
					break;

				case RoutingStateSync sync:
					// “State sync” is authoritative for reconnect healing.
					if (_activeTarget != sync.ActiveTargetPeerId)
					{
						_activeTarget = sync.ActiveTargetPeerId;
						newTarget = _activeTarget;
					}
					if (_activeSource != sync.ActiveSourcePeerId)
					{
						_activeSource = sync.ActiveSourcePeerId;
						newSource = _activeSource;
					}
					break;
			}
		}

		if (newTarget is not null) ActiveTargetChanged?.Invoke(this, newTarget);
		if (newSource is not null) ActiveSourceChanged?.Invoke(this, newSource);
	}
}
