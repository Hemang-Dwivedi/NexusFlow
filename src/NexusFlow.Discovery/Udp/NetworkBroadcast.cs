using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NexusFlow.Discovery.Udp;

internal static class NetworkBroadcast
{
	public static IReadOnlyList<IPEndPoint> GetBroadcastEndpoints(int port)
	{
		var endpoints = new List<IPEndPoint>();

		foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
		{
			if (ni.OperationalStatus != OperationalStatus.Up) continue;
			if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

			// Optional: skip obvious virtuals (keep if you use VPN discovery)
			var desc = (ni.Description ?? "").ToLowerInvariant();
			var name = (ni.Name ?? "").ToLowerInvariant();
			if (desc.Contains("virtual") || desc.Contains("hyper-v") || name.Contains("vEthernet".ToLowerInvariant()))
				continue;

			var ipProps = ni.GetIPProperties();
			foreach (var ua in ipProps.UnicastAddresses)
			{
				if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
				if (ua.IPv4Mask is null) continue;

				var ip = ua.Address;
				var mask = ua.IPv4Mask;

				var broadcast = GetDirectedBroadcast(ip, mask);
				endpoints.Add(new IPEndPoint(broadcast, port));
			}
		}

		// As a last resort, also try limited/global broadcast
		endpoints.Add(new IPEndPoint(IPAddress.Broadcast, port));

		// de-dupe
		return endpoints
			.GroupBy(e => e.Address)
			.Select(g => g.First())
			.ToList();
	}

	private static IPAddress GetDirectedBroadcast(IPAddress ip, IPAddress mask)
	{
		var ipBytes = ip.GetAddressBytes();
		var maskBytes = mask.GetAddressBytes();
		var bcast = new byte[4];

		for (int i = 0; i < 4; i++)
			bcast[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 255));

		return new IPAddress(bcast);
	}
}
