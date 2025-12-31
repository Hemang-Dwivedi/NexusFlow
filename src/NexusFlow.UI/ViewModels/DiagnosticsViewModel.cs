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

		// sensible defaults for combo selection
		SelectedTargetPeerId = Peers.LocalPeerId;
		SelectedSourcePeerId = Peers.LocalPeerId;
	}

	public IConnectedPeersSnapshot Peers { get; }

	[ObservableProperty] private string _activeTargetPeerId = "";
	[ObservableProperty] private string _activeSourcePeerId = "";

	[ObservableProperty] private string _selectedTargetPeerId = "";
	[ObservableProperty] private string _selectedSourcePeerId = "";

	[RelayCommand]
	private Task SetTargetAsync() => _routing.RequestSetActiveTargetAsync(SelectedTargetPeerId);

	[RelayCommand]
	private Task SetSourceAsync() => _routing.RequestSetActiveSourceAsync(SelectedSourcePeerId);

	[RelayCommand]
	private Task SetSelfAsync()
	{
		var self = Peers.LocalPeerId;
		SelectedTargetPeerId = self;
		SelectedSourcePeerId = self;
		return Task.WhenAll(
			_routing.RequestSetActiveTargetAsync(self),
			_routing.RequestSetActiveSourceAsync(self)
		);
	}
}

public interface IConnectedPeersSnapshot
{
	string LocalPeerId { get; }
	ObservableCollection<(string PeerId, string DisplayName)> ConnectedPeers { get; }
}
