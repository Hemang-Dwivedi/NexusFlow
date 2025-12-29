namespace NexusFlow.Settings;

public static class SettingsPaths
{
	public static string AppDataDir =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusFlow");

	public static string SettingsFile =>
		Path.Combine(AppDataDir, "settings.json");
}
