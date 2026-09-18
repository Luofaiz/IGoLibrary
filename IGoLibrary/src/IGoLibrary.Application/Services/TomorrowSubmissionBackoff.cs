namespace IGoLibrary.Application.Services;

// One cooldown for the active account's run, shared by all seats and queue sessions.
internal sealed class TomorrowSubmissionBackoff(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private TimeSpan _cooldown;
    private long _lastBusyTimestamp;

    public TimeSpan Remaining
    {
        get
        {
            var remaining = _cooldown - _timeProvider.GetElapsedTime(_lastBusyTimestamp);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public TimeSpan RecordBusy()
    {
        _cooldown = TimeSpan.FromMilliseconds(_cooldown == TimeSpan.Zero
            ? 300
            : Math.Min(_cooldown.TotalMilliseconds * 2, 1000));
        _lastBusyTimestamp = _timeProvider.GetTimestamp();
        return _cooldown;
    }

    public void Reset() => _cooldown = TimeSpan.Zero;
}
