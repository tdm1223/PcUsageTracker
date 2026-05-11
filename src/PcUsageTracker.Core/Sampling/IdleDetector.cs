namespace PcUsageTracker.Core.Sampling;

/// <summary>
/// idle 임계값(threshold)을 보유하고 마지막 Update에서 계산된 idle 상태를 노출한다.
/// 단순한 비교지만 클래스로 추출한 이유: threshold 변경(UI)과 상태 조회(트레이 아이콘/툴팁)를
/// 한 자리에 모아 테스트 가능하게 하고, TrayContext가 두 책임을 겹치지 않게 한다.
/// </summary>
public sealed class IdleDetector
{
    TimeSpan _threshold;
    bool _isIdle;

    public IdleDetector(TimeSpan threshold)
    {
        SetThreshold(threshold);
    }

    public TimeSpan Threshold => _threshold;
    public bool IsIdle => _isIdle;

    public void SetThreshold(TimeSpan threshold)
    {
        if (threshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Idle threshold must be positive.");
        _threshold = threshold;
    }

    /// <summary>현재 idle 경과 시간을 기준으로 IsIdle을 갱신하고 반환한다.</summary>
    public bool Update(TimeSpan idleSince)
    {
        _isIdle = idleSince >= _threshold;
        return _isIdle;
    }
}
