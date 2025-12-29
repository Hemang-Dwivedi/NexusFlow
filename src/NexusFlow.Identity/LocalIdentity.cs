using NexusFlow.Identity;
using NexusFlow.Settings;
using NexusFlow.Settings.Models;

namespace NexusFlow.Identity;

public sealed class LocalIdentity : ILocalIdentity
{
	public string PeerId { get; }
	public string DeviceName { get; }

	public LocalIdentity(ISettingsStore settingsStore)
	{
		DeviceName = Environment.MachineName;

		var state = settingsStore.Load<LocalIdentityState>();

		if (Guid.TryParse(state.PeerId, out var parsed))
		{
			PeerId = parsed.ToString("D");
			return;
		}

		// First run: create + persist
		PeerId = Guid.NewGuid().ToString("D");
		state.PeerId = PeerId;
		settingsStore.Save(state);
	}
}
