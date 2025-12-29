using System.Text.Json;

namespace NexusFlow.Settings.Layout;

public sealed class JsonLayoutStore : ILayoutStore
{
	private static readonly JsonSerializerOptions _json = new()
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	private readonly string _filePath;

	public JsonLayoutStore(string appName = "NexusFlow")
	{
		var dir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			appName);

		Directory.CreateDirectory(dir);
		_filePath = Path.Combine(dir, "layout-state.json");
	}

	public LayoutState Load()
	{
		try
		{
			if (!File.Exists(_filePath))
				return new LayoutState();

			var json = File.ReadAllText(_filePath);
			return JsonSerializer.Deserialize<LayoutState>(json, _json) ?? new LayoutState();
		}
		catch
		{
			// Corrupt file? Start fresh (we can add diagnostics logging later)
			return new LayoutState();
		}
	}

	public void Save(LayoutState state)
	{
		var json = JsonSerializer.Serialize(state, _json);
		File.WriteAllText(_filePath, json);
	}
}
