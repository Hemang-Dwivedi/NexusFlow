namespace NexusFlow.Settings;

public interface ISettingsStore
{
	T Load<T>() where T : new();
	void Save<T>(T model);
}
