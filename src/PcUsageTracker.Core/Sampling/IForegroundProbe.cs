namespace PcUsageTracker.Core.Sampling;

public interface IForegroundProbe
{
    /// <summary>
    /// 현재 포그라운드 프로세스 스냅샷. 관측 불가 시 null.
    /// </summary>
    ForegroundSnapshot? Sample();
}

public static class ForegroundProcessNames
{
    public const string AccessDenied = "__access_denied__";
    public const string Unknown = "__unknown__";
}
