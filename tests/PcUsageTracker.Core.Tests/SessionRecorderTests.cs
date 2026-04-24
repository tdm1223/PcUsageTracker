using FluentAssertions;
using PcUsageTracker.Core.Sampling;

namespace PcUsageTracker.Core.Tests;

public class SessionRecorderTests
{
    sealed class FakeSink : ISessionSink
    {
        public record Event(string Kind, long Id, string? ProcessName, DateTimeOffset At);

        public List<Event> Events { get; } = new();
        long _nextId = 1;

        public long Open(string processName, DateTimeOffset startAt)
        {
            var id = _nextId++;
            Events.Add(new Event("open", id, processName, startAt));
            return id;
        }

        public void Close(long sessionId, DateTimeOffset endAt)
        {
            Events.Add(new Event("close", sessionId, null, endAt));
        }
    }

    static DateTimeOffset T(int seconds) => new(2026, 4, 24, 10, 0, seconds, TimeSpan.Zero);

    [Fact]
    public void first_tick_opens_session()
    {
        var sink = new FakeSink();
        var r = new SessionRecorder(sink);

        r.Tick("chrome", T(0));

        sink.Events.Should().HaveCount(1);
        sink.Events[0].Should().BeEquivalentTo(new FakeSink.Event("open", 1, "chrome", T(0)));
        r.CurrentProcessName.Should().Be("chrome");
    }

    [Fact]
    public void same_process_repeated_tick_is_noop()
    {
        var sink = new FakeSink();
        var r = new SessionRecorder(sink);

        r.Tick("chrome", T(0));
        r.Tick("chrome", T(1));
        r.Tick("chrome", T(2));

        sink.Events.Should().HaveCount(1);
        sink.Events[0].Kind.Should().Be("open");
    }

    [Fact]
    public void process_switch_closes_prev_and_opens_new()
    {
        var sink = new FakeSink();
        var r = new SessionRecorder(sink);

        r.Tick("chrome", T(0));
        r.Tick("chrome", T(1));
        r.Tick("code", T(2));

        sink.Events.Should().HaveCount(3);
        sink.Events[0].Should().BeEquivalentTo(new FakeSink.Event("open", 1, "chrome", T(0)));
        sink.Events[1].Should().BeEquivalentTo(new FakeSink.Event("close", 1, null, T(2)));
        sink.Events[2].Should().BeEquivalentTo(new FakeSink.Event("open", 2, "code", T(2)));
    }

    [Fact]
    public void pause_closes_current_and_ignores_subsequent_ticks()
    {
        var sink = new FakeSink();
        var r = new SessionRecorder(sink);

        r.Tick("chrome", T(0));
        r.Pause(T(5));

        sink.Events.Should().HaveCount(2);
        sink.Events[^1].Should().BeEquivalentTo(new FakeSink.Event("close", 1, null, T(5)));
        r.IsPaused.Should().BeTrue();

        r.Tick("chrome", T(6));
        r.Tick("code", T(7));
        sink.Events.Should().HaveCount(2);
    }

    [Fact]
    public void resume_opens_new_session_on_next_tick()
    {
        var sink = new FakeSink();
        var r = new SessionRecorder(sink);

        r.Tick("chrome", T(0));
        r.Pause(T(5));
        r.Resume();
        r.Tick("chrome", T(10));

        sink.Events.Should().HaveCount(3);
        sink.Events[2].Should().BeEquivalentTo(new FakeSink.Event("open", 2, "chrome", T(10)));
        r.CurrentProcessName.Should().Be("chrome");
    }

    [Fact]
    public void null_process_closes_without_opening()
    {
        var sink = new FakeSink();
        var r = new SessionRecorder(sink);

        r.Tick("chrome", T(0));
        r.Tick(null, T(3));

        sink.Events.Should().HaveCount(2);
        sink.Events[1].Should().BeEquivalentTo(new FakeSink.Event("close", 1, null, T(3)));
        r.CurrentProcessName.Should().BeNull();
    }

    [Fact]
    public void double_pause_is_idempotent()
    {
        var sink = new FakeSink();
        var r = new SessionRecorder(sink);

        r.Tick("chrome", T(0));
        r.Pause(T(1));
        r.Pause(T(2));

        sink.Events.Should().HaveCount(2);
    }

    [Fact]
    public void process_names_are_case_sensitive()
    {
        // Windows process names differ only in case only in rare cases; Ordinal 매칭.
        var sink = new FakeSink();
        var r = new SessionRecorder(sink);

        r.Tick("Chrome", T(0));
        r.Tick("chrome", T(1));

        sink.Events.Should().HaveCount(3);
        sink.Events[0].ProcessName.Should().Be("Chrome");
        sink.Events[2].ProcessName.Should().Be("chrome");
    }
}
