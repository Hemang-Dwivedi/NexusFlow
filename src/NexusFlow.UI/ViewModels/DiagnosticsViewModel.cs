using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusFlow.Core.Routing;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace NexusFlow.UI.ViewModels;

public sealed partial class DiagnosticsViewModel : ObservableObject
{
	private readonly IRoutingEngine _routing;

	public DiagnosticsViewModel(IRoutingEngine routing, IConnectedPeersSnapshot peers)
	{
		_routing = routing;
		Peers = peers;

		ActiveTargetPeerId = _routing.ActiveTargetPeerId;
		ActiveSourcePeerId = _routing.ActiveSourcePeerId;

		_routing.ActiveTargetChanged += (_, id) => ActiveTargetPeerId = id;
		_routing.ActiveSourceChanged += (_, id) => ActiveSourcePeerId = id;

		// Default selections
		SelectedTargetItem = FindByPeerId(Peers.LocalPeerId);
		SelectedSourceItem = FindByPeerId(Peers.LocalPeerId);
	}

	public IConnectedPeersSnapshot Peers { get; }

	[ObservableProperty] private string _activeTargetPeerId = "";
	[ObservableProperty] private string _activeSourcePeerId = "";

	// Bind ComboBox SelectedItem to these (NOT SelectedValuePath)
	[ObservableProperty] private (string PeerId, string DisplayName)? _selectedTargetItem;
	[ObservableProperty] private (string PeerId, string DisplayName)? _selectedSourceItem;

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
