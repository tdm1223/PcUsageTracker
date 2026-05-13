namespace PcUsageTracker.Core.Sampling;

/// <summary>
/// idle 시간을 sessions 테이블에 일반 세션처럼 기록하기 위한 sentinel 프로세스명.
/// 실제 OS 프로세스명과 충돌하지 않도록 더블 언더스코어 prefix/suffix 사용
/// (Windows 프로세스명은 일반적으로 '__'를 포함하지 않음).
/// </summary>
public static class IdleSentinel
{
    public const string Name = "__idle__";
}
