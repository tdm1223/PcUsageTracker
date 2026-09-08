namespace PcUsageTracker.Core.Sampling;

/// <summary>
/// 1Hz 샘플링 틱을 세션 시퀀스로 변환한다. 상태 머신:
///   Sampling <-> Paused (lock / suspend).
/// Pause 진입 시 현재 세션을 닫는다. Resume 후 다음 Tick부터 신규 세션 생성.
/// </summary>
public sealed class SessionRecorder
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ContinuityGapThreshold = TimeSpan.FromSeconds(30);

    readonly ISessionSink _sink;
    (long Id, string ProcessName)? _current;
    DateTimeOffset? _lastHeartbeatAt;
    DateTimeOffset? _lastObservedAt;
    bool _paused;

    public SessionRecorder(ISessionSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public bool IsPaused => _paused;
    public string? CurrentProcessName => _current?.ProcessName;

    /// <summary>1Hz 샘플 한 틱을 기록한다. processName이 null이면 현재 세션을 닫고 아무것도 열지 않는다.</summary>
    public void Tick(string? processName, DateTimeOffset now)
    {
        if (_paused) return;

        // Suspend/shutdown notifications are best-effort. If timer delivery itself stopped for a
        // long interval, split the session at the last real sample instead of counting the gap.
        if (_current is not null && _lastObservedAt is { } lastObserved &&
            now - lastObserved > ContinuityGapThreshold)
        {
            CloseCurrent(lastObserved);
        }

        if (processName is null)
        {
            CloseCurrent(now);
            return;
        }

        if (_current is null)
        {
            var id = _sink.Open(processName, now);
            _current = (id, processName);
            _lastHeartbeatAt = now;
            _lastObservedAt = now;
            return;
        }

        if (!string.Equals(_current.Value.ProcessName, processName, StringComparison.Ordinal))
        {
            _sink.Close(_current.Value.Id, now);
            var id = _sink.Open(processName, now);
            _current = (id, processName);
            _lastHeartbeatAt = now;
            _lastObservedAt = now;
            return;
        }

        // 비정상 종료 시 PC가 꺼진 시간을 마지막 앱에 붙이지 않도록 마지막 관찰 시각을
        // 주기적으로 영속화한다. 매초 쓰지 않고 5초 단위로 제한한다.
        if (_lastHeartbeatAt is null || now - _lastHeartbeatAt.Value >= HeartbeatInterval)
        {
            _sink.Touch(_current.Value.Id, now);
            _lastHeartbeatAt = now;
        }
        _lastObservedAt = now;
    }

    public void Pause(DateTimeOffset at)
    {
        if (_paused) return;
        var closeAt = _lastObservedAt is { } lastObserved &&
                      at - lastObserved > ContinuityGapThreshold
            ? lastObserved
            : at;
        CloseCurrent(closeAt);
        _paused = true;
    }

    public void Resume()
    {
        _paused = false;
    }

    void CloseCurrent(DateTimeOffset at)
    {
        if (_current is { } c)
        {
            _sink.Close(c.Id, at);
            _current = null;
            _lastHeartbeatAt = null;
            _lastObservedAt = null;
        }
    }
}
