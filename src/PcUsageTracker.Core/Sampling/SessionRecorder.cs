namespace PcUsageTracker.Core.Sampling;

/// <summary>
/// 1Hz 샘플링 틱을 세션 시퀀스로 변환한다. 상태 머신:
///   Sampling <-> Paused (lock / suspend).
/// Pause 진입 시 현재 세션을 닫는다. Resume 후 다음 Tick부터 신규 세션 생성.
/// </summary>
public sealed class SessionRecorder
{
    readonly ISessionSink _sink;
    (long Id, string ProcessName)? _current;
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

        if (processName is null)
        {
            CloseCurrent(now);
            return;
        }

        if (_current is null)
        {
            var id = _sink.Open(processName, now);
            _current = (id, processName);
            return;
        }

        if (!string.Equals(_current.Value.ProcessName, processName, StringComparison.Ordinal))
        {
            _sink.Close(_current.Value.Id, now);
            var id = _sink.Open(processName, now);
            _current = (id, processName);
        }
        // 같은 프로세스는 no-op (end_at 업데이트는 세션 종료 시 한 번).
    }

    public void Pause(DateTimeOffset at)
    {
        if (_paused) return;
        CloseCurrent(at);
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
        }
    }
}
