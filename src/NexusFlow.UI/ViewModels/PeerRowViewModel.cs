using CommunityToolkit.Mvvm.ComponentModel;

namespace NexusFlow.App.ViewModels;

public partial class PeerRowViewModel : ObservableObject
{
	[ObservableProperty] private string _peerId;
	[ObservableProperty] private string _deviceName;
	[ObservableProperty] private int _tcpPort;
	[ObservableProperty] private DateTimeOffset _lastSeen;
	public PeerRowViewModel(string peerId, string deviceName, int tcpPort, DateTimeOffset lastSeen)
	{
		PeerId = peerId;
		DeviceName = deviceName;
		TcpPort = tcpPort;
		LastSeen = lastSeen;
	}



	public string PeerIdShort => PeerId.Length >= 8 ? PeerId[..8] : PeerId;
}
