using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using NexusFlow.Core.Input;
using NexusFlow.Core.Input;
using Avalonia.Threading;

namespace NexusFlow.UI.ViewModels;

public sealed partial class DiagnosticsViewModel : ObservableObject
{
	private readonly IRoutingEngine _routing;
	private readonly IDiagnosticsLog _log;
	private readonly IFailsafeService _failsafe;
	private readonly IInputSourceSwitchingSimulator _inputSim;
	private readonly IModifierStateTracker _mods;
	private readonly IOrderedInputRouter _router;
	public DiagnosticsViewModel(
	IRoutingEngine routing,
	IConnectedPeersSnapshot peers,
	IDiagnosticsLog log,
	IFailsafeService failsafe,
	IInputSourceSwitchingSimulator inputSim,
	IModifierStateTracker mods,
	IOrderedInputRouter router)

	{
		_routing = routing;
		Peers = peers;
		_log = log;
		_failsafe = failsafe;
		_inputSim = inputSim;
		_mods = mods;
		ModifierStateText = _mods.Current.ToString();

		_mods.Changed += s => Dispatcher.UIThread.Post(() => ModifierStateText = s.ToString());

		SelectedKeyPeerItem = FindByPeerId(Peers.LocalPeerId);
		SelectedKey = KeyKind.Ctrl;
		_seq = 0;

		InputThresholdInfo = _inputSim.MovementThresholdInfo;

		SelectedSimPeerItem = FindByPeerId(Peers.LocalPeerId);
		MoveDx = 8;
		MoveDy = 0;


		ActiveTargetPeerId = _routing.ActiveTargetPeerId;
		ActiveSourcePeerId = _routing.ActiveSourcePeerId;

		_routing.ActiveTargetChanged += (_, id) => ActiveTargetPeerId = id;
		_routing.ActiveSourceChanged += (_, id) => ActiveSourcePeerId = id;

		IsFailsafeBlocked = _failsafe.IsBlocked;
		_failsafe.Changed += b => Dispatcher.UIThread.Post(() => IsFailsafeBlocked = b);

		// seed logs
		foreach (var e in _log.Snapshot())
			Logs.Add(e);

		_log.Added += e => Dispatcher.UIThread.Post(() => Logs.Add(e));

		SelectedTargetItem = FindByPeerId(Peers.LocalPeerId);
		SelectedSourceItem = FindByPeerId(Peers.LocalPeerId);
		_router = router;
	}

	public IConnectedPeersSnapshot Peers { get; }

	public ObservableCollection<LogEntry> Logs { get; } = new();

	[ObservableProperty] private string _activeTargetPeerId = "";
	[ObservableProperty] private string _activeSourcePeerId = "";

	[ObservableProperty] private bool _isFailsafeBlocked;

	[ObservableProperty] private (string PeerId, string DisplayName)? _selectedTargetItem;
	[ObservableProperty] private (string PeerId, string DisplayName)? _selectedSourceItem;
	[ObservableProperty] private string _inputThresholdInfo = "";

	[ObservableProperty] private (string PeerId, string DisplayName)? _selectedSimPeerItem;

	[ObservableProperty] private double _moveDx;
	[ObservableProperty] private double _moveDy;

	[ObservableProperty] private string _modifierStateText = "";

	[ObservableProperty] private (string PeerId, string DisplayName)? _selectedKeyPeerItem;

	[ObservableProperty] private KeyKind _selectedKey = KeyKind.Ctrl;
	private string KeyPeerIdOrSelf()
	=> SelectedKeyPeerItem?.PeerId ?? Peers.LocalPeerId;

	private SimKeyEvent NextEvent(KeyAction action)
		=> new(++_seq, KeyPeerIdOrSelf(), SelectedKey, action, DateTimeOffset.Now);

	private long _seq;
	public IReadOnlyList<KeyKind> KeyOptions { get; } =
		Enum.GetValues<KeyKind>().ToList();

	private string SimPeerIdOrSelf()
		=> SelectedSimPeerItem?.PeerId ?? Peers.LocalPeerId;

	[RelayCommand]
	private Task SimKeyPressAsync()
		=> _inputSim.SimKeyPressAsync(SimPeerIdOrSelf());

	[RelayCommand]
	private Task SimMouseClickAsync()
		=> _inputSim.SimMouseClickAsync(SimPeerIdOrSelf());

	[RelayCommand]
	private Task SimMouseScrollAsync()
		=> _inputSim.SimMouseScrollAsync(SimPeerIdOrSelf());

	
	[RelayCommand]
	private Task SimMicActivityAsync()
		=> _inputSim.SimMicActivityAsync(SimPeerIdOrSelf());

	[RelayCommand]
	private Task SetTargetAsync()
	{
		var id = SelectedTargetItem?.PeerId ?? "";
		if (string.IsNullOrWhiteSpace(id)) return Task.CompletedTask;
		return _routing.RequestSetActiveTargetAsync(id);
	}

	[RelayCommand]
	private Task SetSourceAsync()
	{
		var id = SelectedSourceItem?.PeerId ?? "";
		if (string.IsNullOrWhiteSpace(id)) return Task.CompletedTask;
		return _routing.RequestSetActiveSourceAsync(id);
	}

	[RelayCommand]
	private Task SetSelfAsync()
	{
		var self = Peers.LocalPeerId;
		SelectedTargetItem = FindByPeerId(self);
		SelectedSourceItem = FindByPeerId(self);

		return Task.WhenAll(
			_routing.RequestSetActiveTargetAsync(self),
			_routing.RequestSetActiveSourceAsync(self)
		);
	}

	[RelayCommand]
	private void ToggleFailsafe() => _failsafe.Toggle();
	[RelayCommand]
	private void SimKeyDown()
	{
		var peer = SelectedSimPeerItem?.PeerId ?? Peers.LocalPeerId;
		_router.EnqueueKey(peer, SelectedKey, KeyAction.Down);
	}

	[RelayCommand]
	private void SimKeyUp()
	{
		var peer = SelectedSimPeerItem?.PeerId ?? Peers.LocalPeerId;
		_router.EnqueueKey(peer, SelectedKey, KeyAction.Up);
	}

	[RelayCommand]
	private void SimMouseMove()
	{
		var peer = SelectedSimPeerItem?.PeerId ?? Peers.LocalPeerId;
		_router.EnqueueMouseMove(peer, MoveDx, MoveDy);
	}

	[RelayCommand]
	private void ResetModifiers()
	{
		_mods.Reset("Manual reset");
	}

	private (string PeerId, string DisplayName)? FindByPeerId(string peerId)
	{
		foreach (var p in Peers.ConnectedPeers)
			if (p.PeerId == peerId) return p;
		return null;
	}
}

public interface IConnectedPeersSnapshot
{
	string LocalPeerId { get; }
	ObservableCollection<(string PeerId, string DisplayName)> ConnectedPeers { get; }
}
