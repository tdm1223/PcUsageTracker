namespace PcUsageTracker.Core.Sampling;

/// <summary>
/// 마지막 사용자 입력(키보드/마우스) 이후 경과 시간을 반환한다.
/// Win32 구현은 GetLastInputInfo + Environment.TickCount 차이를 사용한다.
/// </summary>
public interface IIdleProbe
{
    TimeSpan IdleDuration { get; }
}
