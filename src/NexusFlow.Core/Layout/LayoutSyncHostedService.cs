using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Control;
using NexusFlow.Core.Services;
using NexusFlow.Identity;
using NexusFlow.Protocol.Control;
using NexusFlow.Settings.Layout;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.Core.Layout;

/// <summary>
/// Keeps ILayoutState populated with:
/// - local peer desktop bounds
/// - remote peer desktop bounds (from authenticated Control messages)
///
/// This is the minimum needed so Layout UI can render "connected peers",
/// and auto-switch can reason about boundaries.
/// </summary>
public sealed class LayoutSyncHostedService : IHostedService
{
	private readonly ConnectionManager _connections;
	private readonly DisplayService _displayService;
	private readonly ILayoutState _layout;
	private readonly ILayoutStore _layoutStore;
	private readonly ILocalIdentity _localIdentity;

	public LayoutSyncHostedService(
		ConnectionManager connections,
		DisplayService displayService,
		ILayoutState layout,
		ILayoutStore layoutStore,
		ILocalIdentity localIdentity)
	{
		_connections = connections;
		_displayService = displayService;
		_layout = layout;
		_layoutStore = layoutStore;
		_localIdentity = localIdentity;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		// seed local immediately
		_layout.UpsertPeerRect(BuildLocalPeerRect());

		_connections.PeerConnected += OnPeerConnected;
		_connections.PeerDisconnected += OnPeerDisconnected;
		_connections.ControlMessageReceived += OnControlMessageReceived;

		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_connections.PeerConnected -= OnPeerConnected;
		_connections.PeerDisconnected -= OnPeerDisconnected;
		_connections.ControlMessageReceived -= OnControlMessageReceived;
		return Task.CompletedTask;
	}

	private void OnPeerConnected(ConnectedPeer peer)
	{
		// Send my rect to the newly connected peer (best-effort)
		_ = Task.Run(async () =>
		{
			try
			{
				var mine = BuildLocalPeerRect();
				var msg = new PeerRectSyncV1(
					PeerId: mine.PeerId,
					DeviceName: mine.DeviceName,
					MinX: (int)mine.X,
					MinY: (int)mine.Y,
					Width: (int)mine.Width,
					Height: (int)mine.Height
				);

				await _connections.SendToPeerAsync(peer.PeerId, msg, CancellationToken.None)
					.ConfigureAwait(false);
			}
			catch { }
		});

		// Ensure local stays present
		_layout.UpsertPeerRect(BuildLocalPeerRect());
	}

	private void OnPeerDisconnected(string peerId)
	{
		_layout.RemovePeer(peerId);
	}

	private void OnControlMessageReceived(string peerId, byte[] payload)
	{
		try
		{
			var type = ControlCodec.PeekType(payload);

			if (type == nameof(PeerRectSyncV1))
			{
				var msg = ControlCodec.Decode<PeerRectSyncV1>(payload)!;
				var raw = new PeerRect(
					PeerId: msg.PeerId,
					DeviceName: msg.DeviceName,
					X: msg.MinX,
					Y: msg.MinY,
					Width: msg.Width,
					Height: msg.Height
				);
				var saved = _layoutStore.Load();
				if (saved.Peers.TryGetValue(msg.PeerId, out var peerState) && peerState.HasSavedPosition)
					_layout.UpsertPeerRect(raw with { X = peerState.AppliedOffsetX, Y = peerState.AppliedOffsetY });
				else
					_layout.UpsertPeerRect(raw);
			}
			else if (type == nameof(LayoutPositionSyncV1))
			{
				var msg = ControlCodec.Decode<LayoutPositionSyncV1>(payload)!;

				// Only care if we are the peer being positioned
				if (msg.ForPeerId != _localIdentity.PeerId) return;

				// Find ByPeerId's existing rect (for Width/Height)
				var existing = _layout.Current?.Peers.FirstOrDefault(p => p.PeerId == msg.ByPeerId);
				if (existing == null || existing.PeerId != msg.ByPeerId) return;

				// Our desktop origin in Windows virtual space
				var cluster = _displayService.GetLocalCluster();
				double bMinX = cluster.Displays.Any() ? cluster.Displays.Min(d => (double)d.X) : 0;
				double bMinY = cluster.Displays.Any() ? cluster.Displays.Min(d => (double)d.Y) : 0;

				// ByPeer is to OUR left/above by RelDx/RelDy
				var updated = existing with
				{
					X = bMinX - msg.RelDx,
					Y = bMinY - msg.RelDy
				};
				_layout.UpsertPeerRect(updated);

				// Persist so it survives restart
				var store = _layoutStore.Load();
				if (!store.Peers.TryGetValue(msg.ByPeerId, out var ps))
				{
					ps = new PeerLayoutState();
					store.Peers[msg.ByPeerId] = ps;
				}
				ps.AppliedOffsetX = updated.X;
				ps.AppliedOffsetY = updated.Y;
				ps.HasSavedPosition = true;
				_layoutStore.Save(store);
			}
		}
		catch
		{
			// ignore malformed control payloads
		}
	}

	private PeerRect BuildLocalPeerRect()
	{
		var cluster = _displayService.GetLocalCluster();

		if (cluster.Displays.Count == 0)
			return new PeerRect(cluster.PeerId, 0, 0, 0, 0);

		var minX = cluster.Displays.Min(d => d.X);
		var minY = cluster.Displays.Min(d => d.Y);
		var maxX = cluster.Displays.Max(d => d.X + d.Width);
		var maxY = cluster.Displays.Max(d => d.Y + d.Height);

		return new PeerRect(
			PeerId: cluster.PeerId,
			DeviceName: cluster.PeerName,
			X: minX,
			Y: minY,
			Width: maxX - minX,
			Height: maxY - minY
		);
	}
}
