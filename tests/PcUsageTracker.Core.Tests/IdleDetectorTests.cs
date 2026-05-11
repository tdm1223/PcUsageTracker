using FluentAssertions;
using PcUsageTracker.Core.Sampling;

namespace PcUsageTracker.Core.Tests;

public class IdleDetectorTests
{
    [Fact]
    public void ctor_rejects_zero_threshold()
    {
        Action act = () => new IdleDetector(TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ctor_rejects_negative_threshold()
    {
        Action act = () => new IdleDetector(TimeSpan.FromSeconds(-1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void set_threshold_rejects_zero()
    {
        var d = new IdleDetector(TimeSpan.FromMinutes(3));
        Action act = () => d.SetThreshold(TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void set_threshold_rejects_negative()
    {
        var d = new IdleDetector(TimeSpan.FromMinutes(3));
        Action act = () => d.SetThreshold(TimeSpan.FromSeconds(-1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void update_below_threshold_is_not_idle()
    {
        var d = new IdleDetector(TimeSpan.FromMinutes(3));
        d.Update(TimeSpan.FromMinutes(1)).Should().BeFalse();
        d.IsIdle.Should().BeFalse();
    }

    [Fact]
    public void update_at_threshold_is_idle()
    {
        var d = new IdleDetector(TimeSpan.FromMinutes(3));
        d.Update(TimeSpan.FromMinutes(3)).Should().BeTrue();
        d.IsIdle.Should().BeTrue();
    }

    [Fact]
    public void update_above_threshold_is_idle()
    {
        var d = new IdleDetector(TimeSpan.FromMinutes(3));
        d.Update(TimeSpan.FromMinutes(10)).Should().BeTrue();
        d.IsIdle.Should().BeTrue();
    }

    [Fact]
    public void update_transitions_from_idle_back_to_active()
    {
        var d = new IdleDetector(TimeSpan.FromMinutes(3));
        d.Update(TimeSpan.FromMinutes(5));
        d.IsIdle.Should().BeTrue();

        d.Update(TimeSpan.FromSeconds(1));
        d.IsIdle.Should().BeFalse();
    }

    [Fact]
    public void set_threshold_takes_effect_on_next_update()
    {
        var d = new IdleDetector(TimeSpan.FromMinutes(3));
        d.Update(TimeSpan.FromMinutes(2));
        d.IsIdle.Should().BeFalse();

        d.SetThreshold(TimeSpan.FromMinutes(1));
        d.Update(TimeSpan.FromMinutes(2));
        d.IsIdle.Should().BeTrue();
    }

    [Fact]
    public void threshold_property_reflects_latest_value()
    {
        var d = new IdleDetector(TimeSpan.FromMinutes(3));
        d.Threshold.Should().Be(TimeSpan.FromMinutes(3));

        d.SetThreshold(TimeSpan.FromMinutes(10));
        d.Threshold.Should().Be(TimeSpan.FromMinutes(10));
    }
}
