namespace NexusFlow.Core.Routing;

public interface IRoutingEngine
{
	string ActiveTargetPeerId { get; }
	string ActiveSourcePeerId { get; }

	event EventHandler<string>? ActiveTargetChanged;
	event EventHandler<string>? ActiveSourceChanged;

	Task RequestSetActiveTargetAsync(string targetPeerId, CancellationToken ct = default);
	Task RequestSetActiveSourceAsync(string sourcePeerId, CancellationToken ct = default);

	(string ActiveTargetPeerId, string ActiveSourcePeerId) GetSnapshot();
	void ApplyRemote(object msg);
}
