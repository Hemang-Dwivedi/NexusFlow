using NexusFlow.Display.Models;

namespace NexusFlow.Display.Layout;

public sealed record NormalizedDisplay(
	DisplaySnapshot Source,
	double Nx, double Ny,       // normalized + scaled top-left
	double Nw, double Nh         // normalized + scaled size
);

public sealed record NormalizedCluster(
	PeerDisplayCluster Source,
	double Scale,
	double MinX, double MinY,
	double TotalWidth, double TotalHeight,
	IReadOnlyList<NormalizedDisplay> Displays
);

public static class DisplayLayoutNormalizer
{
	public static NormalizedCluster Normalize(
		PeerDisplayCluster cluster,
		double maxWidth = 900,
		double maxHeight = 400,
		double padding = 10)
	{
		if (cluster.Displays.Count == 0)
			return new NormalizedCluster(cluster, 1, 0, 0, 0, 0, Array.Empty<NormalizedDisplay>());

		var minX = cluster.Displays.Min(d => d.X);
		var minY = cluster.Displays.Min(d => d.Y);
		var maxX = cluster.Displays.Max(d => d.X + d.Width);
		var maxY = cluster.Displays.Max(d => d.Y + d.Height);

		var totalW = maxX - minX;
		var totalH = maxY - minY;

		// Fit into canvas while preserving aspect ratio
		var usableW = Math.Max(1, maxWidth - 2 * padding);
		var usableH = Math.Max(1, maxHeight - 2 * padding);

		var scaleX = usableW / totalW;
		var scaleY = usableH / totalH;
		var scale = Math.Min(scaleX, scaleY);
		scale = Math.Min(scale, 1.0);
		scale = Math.Max(scale, 0.05); // prevent invisible rectangles

		var normalizedW = totalW * scale;
		var normalizedH = totalH * scale;

		// center within maxWidth/maxHeight
		var offsetX = (maxWidth - normalizedW) / 2.0;
		var offsetY = (maxHeight - normalizedH) / 2.0;

		// keep at least small padding
		offsetX = Math.Max(offsetX, padding);
		offsetY = Math.Max(offsetY, padding);

		var normalized = cluster.Displays
			.Select(d => new NormalizedDisplay(
				d,
				Nx: offsetX + (d.X - minX) * scale,
				Ny: offsetY + (d.Y - minY) * scale,
				Nw: d.Width * scale,
				Nh: d.Height * scale
			))
			.ToList();

		return new NormalizedCluster(cluster, scale, minX, minY, normalizedW, normalizedH, normalized);

	}
}
