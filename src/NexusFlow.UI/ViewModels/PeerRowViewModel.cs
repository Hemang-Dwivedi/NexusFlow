using System;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NexusFlow.UI.ViewModels;

public partial class PeerRowViewModel : ObservableObject
{
	public PeerRowViewModel(string peerId, string deviceName, int tcpPort, DateTimeOffset lastSeen, IPAddress address, bool IsTrusted)
	{
		_peerId = peerId;
		_deviceName = deviceName;
		_tcpPort = tcpPort;
		_lastSeen = lastSeen;
		_address = address;
		_isTrusted = IsTrusted;
	}

	[ObservableProperty] private string _peerId;
	[ObservableProperty] private string _deviceName;
	[ObservableProperty] private int _tcpPort;
	[ObservableProperty] private DateTimeOffset _lastSeen;
	[ObservableProperty] private IPAddress _address;
	[ObservableProperty] private bool _isTrusted;
	[ObservableProperty] private DateTimeOffset? _trustedAtUtc;

	public string PeerIdShort => PeerId.Length >= 8 ? PeerId[..8] : PeerId;
	partial void OnPeerIdChanged(string value) => OnPropertyChanged(nameof(PeerIdShort));
}
