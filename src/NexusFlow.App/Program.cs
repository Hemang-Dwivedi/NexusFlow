using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusFlow.App.Services;
using NexusFlow.App.Views;
using NexusFlow.Core.Discovery;
using NexusFlow.Core.Services;
using NexusFlow.Core.Trust;
using NexusFlow.Display.Windows;
using NexusFlow.Identity;
using NexusFlow.Settings;
using NexusFlow.Settings.Layout;
using NexusFlow.Trust;
using NexusFlow.UI.ViewModels;
using System;
using System.IO;


namespace NexusFlow.App
{
	internal sealed class Program
	{
		public static IHost? AppHost { get; private set; }

		[STAThread]
		public static void Main(string[] args)
		{
			AppHost = CreateHostBuilder(args).Build();

			BuildAvaloniaApp()
				.StartWithClassicDesktopLifetime(args);

			AppHost.Dispose();
			AppHost = null;
		}

		private static IHostBuilder CreateHostBuilder(string[] args) =>
			Host.CreateDefaultBuilder(args)
				.ConfigureServices((ctx, services) =>
				{
					// Identity (stable PeerId later; for now ensure you have an implementation)
					services.AddSingleton<ILocalIdentity, LocalIdentity>();
					services.AddSingleton<ISettingsStore>(_ =>
						new JsonSettingsStore(SettingsPaths.SettingsFile));
					services.AddSingleton<ILocalIdentity, LocalIdentity>();

					services.AddSingleton<LayoutEditorViewModel>();
					services.AddSingleton<PeersListViewModel>();
					services.AddSingleton<MainViewModel>();
					services.AddSingleton<WindowsDisplayTopologyProvider>();
					services.AddSingleton<DisplayService>(sp =>
					{
						var provider = sp.GetRequiredService<WindowsDisplayTopologyProvider>();
						return new DisplayService(provider);
					});

					// Layout persistence store (your existing JsonLayoutStore usage)
					services.AddSingleton<JsonLayoutStore>(_ => new JsonLayoutStore("NexusFlow"));

					// UI viewmodels
					services.AddSingleton<LayoutEditorViewModel>(sp =>
					{
						var displayService = sp.GetRequiredService<DisplayService>();
						var store = sp.GetRequiredService<JsonLayoutStore>();
						return new LayoutEditorViewModel(displayService, store);
					});

					services.AddSingleton<PeersListViewModel>(); // this must be the discovery-backed version
					services.AddSingleton<MainViewModel>();

					// MainWindow
					services.AddSingleton<MainWindow>();
					// Core discovery coordinator
					services.AddSingleton(sp =>
					{
						var identity = sp.GetRequiredService<ILocalIdentity>();

						// TODO: move to NexusFlow.Settings
						const int tcpPort = 49800;

						return new DiscoveryCoordinator(identity, tcpPort);
					});

					// Start discovery in background
					services.AddHostedService<DiscoveryHostedService>();

					services.AddSingleton<PairingCoordinator>();
					services.AddSingleton(sp =>
					{
						// Reuse the discovered TcpPort (or choose a dedicated one if you prefer)
						var me = sp.GetRequiredService<ILocalIdentity>();
						// If you already have a configured port provider, use it. For now hardcode or reuse your discovery tcp port.
						return new PairingListener(me, port: 49800);
					});
					services.AddHostedService<PairingHostedService>();
					services.AddSingleton<NexusFlow.UI.Services.IPairingDialogService, NexusFlow.App.Services.PairingDialogService>();
					services.AddSingleton(sp =>
					{
						var baseDir = SettingsPaths.AppDataDir; // property
						var dir = Path.Combine(baseDir, "NexusFlow");
						Directory.CreateDirectory(dir);
						return new TrustStore(Path.Combine(dir, "trust-store.bin"));
					});
					services.AddSingleton<PairingCoordinator>();
					services.AddSingleton<PairingListener>(sp =>
					{
						var me = sp.GetRequiredService<ILocalIdentity>();
						// Use the same tcp port you advertise in discovery for now.
						// If your identity has a configured port, use it.
						return new PairingListener(me, port: 49800);
					});
					services.AddHostedService<PairingHostedService>();

				});

		public static AppBuilder BuildAvaloniaApp()
			=> AppBuilder.Configure<App>()
				.UsePlatformDetect()
				.WithInterFont()
				.LogToTrace();
	}
}
