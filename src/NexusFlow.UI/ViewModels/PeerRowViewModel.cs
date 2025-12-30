using System;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NexusFlow.UI.ViewModels;

public partial class PeerRowViewModel : ObservableObject
{
	public PeerRowViewModel(string peerId, string deviceName, int tcpPort, DateTimeOffset lastSeen, IPAddress address)
	{
		_peerId = peerId;
		_deviceName = deviceName;
		_tcpPort = tcpPort;
		_lastSeen = lastSeen;
		_address = address;
	}

	[ObservableProperty] private string _peerId;
	[ObservableProperty] private string _deviceName;
	[ObservableProperty] private int _tcpPort;
	[ObservableProperty] private DateTimeOffset _lastSeen;
	[ObservableProperty] private IPAddress _address;

	public string PeerIdShort => PeerId.Length >= 8 ? PeerId[..8] : PeerId;
	partial void OnPeerIdChanged(string value) => OnPropertyChanged(nameof(PeerIdShort));
}
