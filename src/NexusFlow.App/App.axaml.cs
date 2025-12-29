using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Hosting;
using NexusFlow.App.Views;

namespace NexusFlow.App
{
	public partial class App : Application
	{
		public override void Initialize()
		{
			AvaloniaXamlLoader.Load(this);
		}

		public override async void OnFrameworkInitializationCompleted()
		{
			if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
			{
				DisableAvaloniaDataAnnotationValidation();

				// Start background services (discovery)
				if (Program.AppHost is not null)
					await Program.AppHost.StartAsync();

				desktop.MainWindow = new MainWindow { };

				desktop.Exit += async (_, __) =>
				{
					if (Program.AppHost is not null)
						await Program.AppHost.StopAsync();
				};
			}

			base.OnFrameworkInitializationCompleted();
		}

		private void DisableAvaloniaDataAnnotationValidation()
		{
			var dataValidationPluginsToRemove =
				BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

			foreach (var plugin in dataValidationPluginsToRemove)
				BindingPlugins.DataValidators.Remove(plugin);
		}
	}
}
