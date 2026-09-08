namespace PcUsageTracker.Core.Sampling;

public interface ISessionSink
{
    /// <summary>새 세션을 연다. 반환 값은 해당 세션의 내부 식별자(SqliteStore에서는 rowid).</summary>
    long Open(string processName, DateTimeOffset startAt);

    /// <summary>진행 중 세션이 실제로 관찰된 마지막 시각을 갱신한다.</summary>
    void Touch(long sessionId, DateTimeOffset observedAt);

    /// <summary>진행 중 세션을 닫는다.</summary>
    void Close(long sessionId, DateTimeOffset endAt);
}
