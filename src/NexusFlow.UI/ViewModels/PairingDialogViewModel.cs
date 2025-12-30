using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace NexusFlow.UI.ViewModels;

public sealed partial class PairingDialogViewModel : ObservableObject
{
	public string Title { get; }
	public string RemoteDeviceName { get; }
	public string Code { get; }
	public string HintText => "Compare this 6-digit code on both devices. Accept only if it matches.";

	public event Action<object?>? CloseRequested;

	public PairingDialogViewModel(string title, string remoteDeviceName, string code)
	{
		Title = title;
		RemoteDeviceName = remoteDeviceName;
		Code = code;
	}

	[RelayCommand]
	private void Accept() => CloseRequested?.Invoke(true);

	[RelayCommand]
	private void Reject() => CloseRequested?.Invoke(false);
}
