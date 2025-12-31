using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusFlow.App.Services;
using NexusFlow.App.Views;
using NexusFlow.Core.Control;
using NexusFlow.Core.Discovery;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;
using NexusFlow.Core.Transport;
using NexusFlow.Core.Trust;
using NexusFlow.Display.Windows;
using NexusFlow.Identity;
using NexusFlow.Settings;
using NexusFlow.Settings.Layout;
using NexusFlow.Trust;
using NexusFlow.UI.Services;
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
					// ---------- Settings / Identity ----------
					services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(SettingsPaths.SettingsFile));
					services.AddSingleton<ILocalIdentity, LocalIdentity>();

					// TrustStore (single instance)
					services.AddSingleton(sp =>
					{
						var baseDir = SettingsPaths.AppDataDir;
						var dir = Path.Combine(baseDir, "NexusFlow");
						Directory.CreateDirectory(dir);
						return new TrustStore(Path.Combine(dir, "trust-store.bin"));
					});

					// ---------- Display / Layout ----------
					services.AddSingleton<WindowsDisplayTopologyProvider>();
					services.AddSingleton<DisplayService>(sp =>
					{
						var provider = sp.GetRequiredService<WindowsDisplayTopologyProvider>();
						return new DisplayService(provider);
					});

					services.AddSingleton<JsonLayoutStore>(_ => new JsonLayoutStore("NexusFlow"));

					// ---------- Core: Discovery ----------
					const int tcpPort = 49800;

					services.AddSingleton(sp =>
					{
						var identity = sp.GetRequiredService<ILocalIdentity>();
						return new DiscoveryCoordinator(identity, tcpPort);
					});
					services.AddHostedService<DiscoveryHostedService>();

					// ---------- Transport / Mux ----------
					services.AddSingleton(_ => new TcpMuxHost(tcpPort));
					services.AddHostedService<TcpMuxHostedService>();

					// ---------- Pairing / Trust ----------
					services.AddSingleton<PairingCoordinator>();
					services.AddSingleton<PairingListener>(sp =>
					{
						var me = sp.GetRequiredService<ILocalIdentity>();
						return new PairingListener(me);
					});
					services.AddSingleton<NexusFlow.UI.Services.IPairingDialogService, NexusFlow.App.Services.PairingDialogService>();

					// ---------- Control Channel (ConnectionManager) ----------
					// NOTE: ConnectionManager ctor = (ILocalIdentity me, TrustStore trustStore)
					services.AddSingleton<ConnectionManager>();

					// ---------- Routing (needs localPeerId + IControlBroadcaster) ----------
					// ConnectionManager implements IControlBroadcaster in your updated file.
					services.AddSingleton<IRoutingEngine>(sp =>
					{
						var me = sp.GetRequiredService<ILocalIdentity>();
						var control = sp.GetRequiredService<ConnectionManager>(); // implements IControlBroadcaster
						return new RoutingEngine(me.PeerId, control);
					});

					services.AddHostedService<RoutingWireupHostedService>();



					// ---------- UI: Connected peer snapshot for Diagnostics ----------
					services.AddSingleton<IConnectedPeersSnapshot, ConnectedPeersSnapshot>();

					// ---------- ViewModels ----------
					services.AddSingleton<LayoutEditorViewModel>(sp =>
					{
						var displayService = sp.GetRequiredService<DisplayService>();
						var store = sp.GetRequiredService<JsonLayoutStore>();
						return new LayoutEditorViewModel(displayService, store);
					});

					services.AddSingleton<PeersListViewModel>();        // assumes it resolves its own deps via DI
					services.AddSingleton<DiagnosticsViewModel>();
					services.AddSingleton<MainViewModel>();

					// ---------- Window ----------
					services.AddSingleton<MainWindow>();
				});


		public static AppBuilder BuildAvaloniaApp()
			=> AppBuilder.Configure<App>()
				.UsePlatformDetect()
				.WithInterFont()
				.LogToTrace();
	}
}
