using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusFlow.App.Services;
using NexusFlow.App.Views;
using NexusFlow.Core.Control;
using NexusFlow.Core.Discovery;
using NexusFlow.Core.InputInjection;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;
using NexusFlow.Core.Transport;
using NexusFlow.Core.Trust;
using NexusFlow.Discovery.Peers;
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


					services.AddHostedService<RoutingWireupHostedService>();


					services.AddSingleton<NexusFlow.Core.Diagnostics.IDiagnosticsLog, NexusFlow.Core.Diagnostics.DiagnosticsLogService>();
					services.AddSingleton<NexusFlow.Core.Services.IFailsafeService, NexusFlow.Core.Services.FailsafeService>();

					// ---------- UI: Connected peer snapshot for Diagnostics ----------
					services.AddSingleton<IConnectedPeersSnapshot, ConnectedPeersSnapshot>();

					// ---------- ViewModels ----------

					services.AddSingleton<NexusFlow.Core.Layout.IRuntimeLayoutState, NexusFlow.Core.Layout.RuntimeLayoutState>();
					services.AddSingleton<LayoutEditorViewModel>(sp =>
					{
						var displayService = sp.GetRequiredService<DisplayService>();
						var store = sp.GetRequiredService<JsonLayoutStore>();
						var runtime = sp.GetRequiredService<IConnectedPeersSnapshot>();
						return new LayoutEditorViewModel(displayService, store, runtime);
					});


					services.AddSingleton<PeersListViewModel>();        // assumes it resolves its own deps via DI
					services.AddSingleton<DiagnosticsViewModel>();
					services.AddSingleton<MainViewModel>();

					// ---------- Window ----------
					services.AddSingleton<MainWindow>();

					// NexusFlow.Input
					services.AddSingleton<NexusFlow.Input.GlobalHotkeyListener>();

					// Hosted service
					services.AddHostedService<NexusFlow.App.Services.FailsafeHotkeyHostedService>();

					services.AddSingleton<NexusFlow.Core.Input.IInputSourceSwitchingSimulator, NexusFlow.Core.Input.InputSourceSwitchingSimulator>();

					// Capture
					services.AddSingleton<NexusFlow.Input.IWinHookCaptureService, NexusFlow.Input.WinHookCaptureService>();
					services.AddHostedService<NexusFlow.App.Hosted.LocalInputCaptureHostedService>();

					// Orchestrator lives in Core
					services.AddSingleton<RoutingEngine>(sp =>
					{
						var me = sp.GetRequiredService<ILocalIdentity>();
						var control = sp.GetRequiredService<ConnectionManager>();
						var failsafe = sp.GetRequiredService<NexusFlow.Core.Services.IFailsafeService>();
						var log = sp.GetRequiredService<NexusFlow.Core.Diagnostics.IDiagnosticsLog>();
						return new RoutingEngine(me.PeerId, control, failsafe, log);
					});

					// Make IRoutingEngine resolve to the same instance
					services.AddSingleton<IRoutingEngine>(sp => sp.GetRequiredService<RoutingEngine>());
					services.AddSingleton<NexusFlow.Core.Input.LocalInputCaptureOrchestrator>();
					services.AddSingleton<NexusFlow.Core.InputTransport.InputReceiver>();
					
					services.AddSingleton<NexusFlow.Discovery.Peers.PeerRegistry>(sp =>
					{
						// choose a sensible “peer stale” TTL (example: 15 seconds)
						var expiry = TimeSpan.FromSeconds(3600);
						var sweep = TimeSpan.FromSeconds(15);
						return new NexusFlow.Discovery.Peers.PeerRegistry(expiry, sweep);
					});
					services.AddSingleton<NexusFlow.Core.Discovery.IPeerEndpointResolver, PeerEndpointResolver>();

					services.AddSingleton<NexusFlow.Core.InputTransport.InputSender>();
					services.AddSingleton(sp =>
					{
						var identity = sp.GetRequiredService<ILocalIdentity>();
						var registry = sp.GetRequiredService<PeerRegistry>();
						return new DiscoveryCoordinator(identity, tcpPort, registry);

					});
					services.AddSingleton<NexusFlow.Core.InputTransport.OrderedInputRouter>();

					services.AddSingleton<NexusFlow.Core.InputTransport.IInputAuthKeyProvider>(sp =>
						sp.GetRequiredService<ConnectionManager>());

					services.AddSingleton<NexusFlow.Core.InputTransport.IRemoteInputSink, NexusFlow.Core.InputTransport.DiagnosticsRemoteInputSink>();
					// ---------- Input Injection (F.7) ----------
					services.AddSingleton<IInputInjector, WindowsSendInputInjector>();
					// Core layout runtime
					services.AddSingleton<NexusFlow.Core.Layout.ILayoutState, NexusFlow.Core.Layout.LayoutState>();

					// Cursor tracker
					services.AddSingleton<NexusFlow.Input.ICursorTracker, NexusFlow.Input.CursorTracker>();

					// Auto target switching engine
					services.AddSingleton<NexusFlow.Core.Routing.TargetSwitchingEngine>();
					services.AddHostedService<NexusFlow.App.Hosted.TargetSwitchingHostedService>();
					services.AddSingleton<NexusFlow.Input.ICursorTracker, NexusFlow.Input.CursorTracker>();

					services.AddSingleton(sp =>
					{
						var display = sp.GetRequiredService<DisplayService>();
						return display.GetLocalCluster(); // PeerDisplayCluster
					});

					services.AddSingleton<NexusFlow.Core.Input.TargetSwitchingEngine>();

				});


		public static AppBuilder BuildAvaloniaApp()
			=> AppBuilder.Configure<App>()
				.UsePlatformDetect()
				.WithInterFont()
				.LogToTrace();
	}
}
