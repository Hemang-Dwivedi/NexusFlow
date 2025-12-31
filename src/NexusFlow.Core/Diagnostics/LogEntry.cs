namespace NexusFlow.Core.Diagnostics;

public enum LogLevel { Trace, Info, Warn, Error }

public sealed record LogEntry(
	DateTimeOffset Timestamp,
	LogLevel Level,
	string Category,
	string Message
);
