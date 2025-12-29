using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusFlow.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
	private readonly string _filePath;

	private static readonly JsonSerializerOptions JsonOpts = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public JsonSettingsStore(string filePath)
	{
		_filePath = filePath;
		Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
	}

	public T Load<T>() where T : new()
	{
		if (!File.Exists(_filePath))
			return new T();

		try
		{
			var json = File.ReadAllText(_filePath);
			return JsonSerializer.Deserialize<T>(json, JsonOpts) ?? new T();
		}
		catch
		{
			// If corrupted, fail safe: return defaults (you can add backup logic later)
			return new T();
		}
	}

	public void Save<T>(T model)
	{
		var json = JsonSerializer.Serialize(model, JsonOpts);
		File.WriteAllText(_filePath, json);
	}
}
