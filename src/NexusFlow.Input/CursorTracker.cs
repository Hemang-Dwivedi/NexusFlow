using System.Threading;

namespace NexusFlow.Input;

public interface ICursorTracker
{
	event Action<int, int, int, int, long>? Moved;
}

public sealed class CursorTracker : ICursorTracker, IDisposable
{
	private readonly IWinHookCaptureService _capture;

	// throttle to ~60Hz max
	private static readonly long MinTicks = TimeSpan.FromMilliseconds(16).Ticks;
	private long _lastTicks;

	public event Action<int, int, int, int, long>? Moved;

	public CursorTracker(IWinHookCaptureService capture)
	{
		_capture = capture;
		_capture.MouseMove += OnMove;
	}

	private void OnMove(CapturedMouseMoveEvent e)
	{
		var last = Volatile.Read(ref _lastTicks);
		if (e.TimestampUtcTicks - last < MinTicks) return;
		Volatile.Write(ref _lastTicks, e.TimestampUtcTicks);

		Moved?.Invoke(e.X, e.Y, e.Dx, e.Dy, e.TimestampUtcTicks);
	}

	public void Dispose()
	{
		_capture.MouseMove -= OnMove;
	}
}
