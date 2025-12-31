using System.Collections.Concurrent;

namespace NexusFlow.Core.Diagnostics;

public sealed class DiagnosticsLogService : IDiagnosticsLog
{
	private readonly int _capacity;
	private readonly ConcurrentQueue<LogEntry> _q = new();
	private readonly object _trimGate = new();

	public event Action<LogEntry>? Added;

	public DiagnosticsLogService(int capacity = 500)
	{
		_capacity = Math.Max(50, capacity);
	}

	public IReadOnlyList<LogEntry> Snapshot()
		=> _q.ToArray();

	public void Write(LogLevel level, string category, string message)
	{
		var e = new LogEntry(DateTimeOffset.Now, level, category, message);
		_q.Enqueue(e);

		// trim
		lock (_trimGate)
		{
			while (_q.Count > _capacity && _q.TryDequeue(out _)) { }
		}

		Added?.Invoke(e);
	}
}
