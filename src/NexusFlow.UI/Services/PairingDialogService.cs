using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using NexusFlow.UI.Services;
using NexusFlow.UI.ViewModels;
using NexusFlow.UI.Views;

namespace NexusFlow.App.Services;

public sealed class PairingDialogService : IPairingDialogService
{
	public async Task<bool> ShowCompareCodeAsync(PairingDialogViewModel vm, CancellationToken ct)
	{
		var lifetime = (IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
		var owner = lifetime.MainWindow!;

		var win = new PairingDialogWindow
		{
			DataContext = vm,
			WindowStartupLocation = WindowStartupLocation.CenterOwner
		};

		// CloseRequested comes from VM
		vm.CloseRequested += result => win.Close(result);

		var resultObj = await win.ShowDialog<object?>(owner);
		return resultObj is bool b && b;
	}
}
