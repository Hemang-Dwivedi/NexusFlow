using System;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusFlow.App.Services;
using NexusFlow.Core.Discovery;
using NexusFlow.Identity;

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
				});

		public static AppBuilder BuildAvaloniaApp()
			=> AppBuilder.Configure<App>()
				.UsePlatformDetect()
				.WithInterFont()
				.LogToTrace();
	}
}
