using Systema.Core;

namespace Systema.Tests;

public class ReEnforceCounterTests
{
    // ── Basic counting ────────────────────────────────────────────────────────

    [Fact]
    public void FirstRecord_ReturnsFalse()
    {
        var counter = new ReEnforceCounter();
        bool hit = counter.Record(1234, TimeSpan.FromSeconds(60), 3);
        Assert.False(hit);
        Assert.Equal(1, counter.GetCount(1234));
    }

    [Fact]
    public void BelowThreshold_NeverReturnsTrue()
    {
        var counter = new ReEnforceCounter();
        Assert.False(counter.Record(1, TimeSpan.FromSeconds(60), 3)); // 1
        Assert.False(counter.Record(1, TimeSpan.FromSeconds(60), 3)); // 2
        Assert.Equal(2, counter.GetCount(1));
    }

    [Fact]
    public void ExactlyAtThreshold_ReturnsTrue()
    {
        var counter = new ReEnforceCounter();
        counter.Record(1, TimeSpan.FromSeconds(60), 3); // 1
        counter.Record(1, TimeSpan.FromSeconds(60), 3); // 2
        bool hit = counter.Record(1, TimeSpan.FromSeconds(60), 3); // 3 → should fire
        Assert.True(hit);
        Assert.Equal(3, counter.GetCount(1));
    }

    [Fact]
    public void BeyondThreshold_ContinuesToReturnTrue()
    {
        var counter = new ReEnforceCounter();
        for (int i = 0; i < 3; i++)
            counter.Record(1, TimeSpan.FromSeconds(60), 3);

        // Extra records after threshold still return true
        Assert.True(counter.Record(1, TimeSpan.FromSeconds(60), 3));
        Assert.True(counter.Record(1, TimeSpan.FromSeconds(60), 3));
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsCount()
    {
        var counter = new ReEnforceCounter();
        counter.Record(1, TimeSpan.FromSeconds(60), 3);
        counter.Record(1, TimeSpan.FromSeconds(60), 3);
        counter.Reset(1);
        Assert.Equal(0, counter.GetCount(1));
    }

    [Fact]
    public void Reset_AfterResetCountStartsFromOne()
    {
        var counter = new ReEnforceCounter();
        counter.Record(1, TimeSpan.FromSeconds(60), 3);
        counter.Record(1, TimeSpan.FromSeconds(60), 3);
        counter.Reset(1);

        bool hit = counter.Record(1, TimeSpan.FromSeconds(60), 3);
        Assert.False(hit);
        Assert.Equal(1, counter.GetCount(1));
    }

    [Fact]
    public void Reset_UnknownPid_DoesNotThrow()
    {
        var counter = new ReEnforceCounter();
        counter.Reset(9999); // should not throw
        Assert.Equal(0, counter.GetCount(9999));
    }

    // ── Window expiry ─────────────────────────────────────────────────────────

    [Fact]
    public void ExpiredWindow_ResetsCountToOne()
    {
        var counter = new ReEnforceCounter();
        // Record twice with a very short window — then record again after the window would expire
        counter.Record(1, TimeSpan.FromMilliseconds(1), 3); // count = 1, window starts
        counter.Record(1, TimeSpan.FromMilliseconds(1), 3); // count = 2

        // Sleep just long enough for the window to expire
        Thread.Sleep(20);

        // Next record should open a fresh window starting at 1, not 3
        bool hit = counter.Record(1, TimeSpan.FromMilliseconds(1), 3);
        Assert.False(hit);
        Assert.Equal(1, counter.GetCount(1));
    }

    [Fact]
    public void WithinWindow_CountAccumulates()
    {
        var counter = new ReEnforceCounter();
        // Use a generous window — all three records are within it
        counter.Record(1, TimeSpan.FromSeconds(60), 3);
        counter.Record(1, TimeSpan.FromSeconds(60), 3);
        bool hit = counter.Record(1, TimeSpan.FromSeconds(60), 3);
        Assert.True(hit);
    }

    // ── Multiple PIDs ─────────────────────────────────────────────────────────

    [Fact]
    public void DifferentPids_TrackIndependently()
    {
        var counter = new ReEnforceCounter();
        counter.Record(1, TimeSpan.FromSeconds(60), 3);
        counter.Record(1, TimeSpan.FromSeconds(60), 3);
        counter.Record(2, TimeSpan.FromSeconds(60), 3); // different PID

        Assert.Equal(2, counter.GetCount(1));
        Assert.Equal(1, counter.GetCount(2));
    }

    [Fact]
    public void ResetOnePid_DoesNotAffectOther()
    {
        var counter = new ReEnforceCounter();
        counter.Record(1, TimeSpan.FromSeconds(60), 3);
        counter.Record(2, TimeSpan.FromSeconds(60), 3);
        counter.Reset(1);

        Assert.Equal(0, counter.GetCount(1));
        Assert.Equal(1, counter.GetCount(2));
    }

    // ── Threshold edge cases ──────────────────────────────────────────────────

    [Fact]
    public void ThresholdOfOne_FiresOnFirstRecord()
    {
        var counter = new ReEnforceCounter();
        bool hit = counter.Record(1, TimeSpan.FromSeconds(60), 1);
        Assert.True(hit);
    }

    [Fact]
    public void UnknownPid_GetCountReturnsZero()
    {
        var counter = new ReEnforceCounter();
        Assert.Equal(0, counter.GetCount(99999));
    }
}
