namespace NexusFlow.Core.Diagnostics;

public interface IDiagnosticsLog
{
	event Action<LogEntry>? Added;

	IReadOnlyList<LogEntry> Snapshot();

	void Write(LogLevel level, string category, string message);
	void Trace(string category, string message) => Write(LogLevel.Trace, category, message);
	void Info(string category, string message) => Write(LogLevel.Info, category, message);
	void Warn(string category, string message) => Write(LogLevel.Warn, category, message);
	void Error(string category, string message) => Write(LogLevel.Error, category, message);
}
