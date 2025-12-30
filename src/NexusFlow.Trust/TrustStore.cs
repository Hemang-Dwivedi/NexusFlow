using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexusFlow.Trust;

public sealed class TrustStore
{
	private readonly string _path;
	private static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

	public TrustStore(string path)
	{
		_path = path;
		Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
	}

	public TrustState Load()
	{
		if (!File.Exists(_path)) return new TrustState();

		try
		{
			var protectedBytes = File.ReadAllBytes(_path);
			var jsonBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
			return JsonSerializer.Deserialize<TrustState>(jsonBytes, Opts) ?? new TrustState();
		}
		catch
		{
			return new TrustState();
		}
	}

	public void Save(TrustState state)
	{
		var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(state, Opts);
		var protectedBytes = ProtectedData.Protect(jsonBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
		File.WriteAllBytes(_path, protectedBytes);
	}
}
