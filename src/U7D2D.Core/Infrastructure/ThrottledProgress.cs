using System.Diagnostics;

namespace U7D2D.Core.Infrastructure;

/// <summary>
/// Filters progress reports on the producing thread before they are posted to the UI.
/// The UI handler used to drop excessive ticks, but every tick still had to be dequeued on the
/// renderer first, which is what made the window unresponsive during a large log.
/// </summary>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly IProgress<T> _inner;
    private readonly long _intervalTicks;
    private readonly Func<T, string>? _keySelector;
    private readonly object _gate = new();

    private long _lastTicks;
    private string? _lastKey;

    public ThrottledProgress(IProgress<T> inner, int intervalMilliseconds = 120, Func<T, string>? keySelector = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _intervalTicks = Math.Max(0, intervalMilliseconds) * Stopwatch.Frequency / 1000;
        _keySelector = keySelector;
    }

    public void Report(T value)
    {
        var key = _keySelector?.Invoke(value);
        var now = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            var stageChanged = key is not null && !string.Equals(key, _lastKey, StringComparison.Ordinal);
            if (!stageChanged && _lastTicks != 0 && now - _lastTicks < _intervalTicks) return;

            _lastTicks = now;
            _lastKey = key;
        }

        _inner.Report(value);
    }
}
