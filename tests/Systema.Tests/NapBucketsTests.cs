using System.Linq;
using Systema.Core;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Unit tests for <see cref="NapBuckets"/> — the single source of truth for which
/// nap category a PID is in. These lock in the invariants the old four-HashSet design
/// relied on every wake/cleanup site to uphold by hand.
/// </summary>
public class NapBucketsTests
{
    [Fact]
    public void NewBuckets_AreEmpty()
    {
        var b = new NapBuckets();
        Assert.Equal(0, b.Count);
        Assert.False(b.IsNapped(123));
        Assert.Null(b.Get(123));
    }

    [Fact]
    public void Mark_RecordsCategory()
    {
        var b = new NapBuckets();
        b.Mark(100, NapReason.Minimized);

        Assert.True(b.IsNapped(100));
        Assert.True(b.Is(100, NapReason.Minimized));
        Assert.Equal(NapReason.Minimized, b.Get(100));
        Assert.Equal(1, b.Count);
    }

    [Fact]
    public void Is_OnlyTrueForTheActualCategory()
    {
        var b = new NapBuckets();
        b.Mark(100, NapReason.Tray);

        Assert.True(b.Is(100, NapReason.Tray));
        Assert.False(b.Is(100, NapReason.Minimized));
        Assert.False(b.Is(100, NapReason.Background));
        Assert.False(b.Is(100, NapReason.Idle));
    }

    [Fact]
    public void Mark_Again_ReplacesCategory_NeverDuplicates()
    {
        // The core invariant the four-HashSet design could violate: a PID must be in
        // exactly ONE category. Re-marking moves it; it never lives in two at once.
        var b = new NapBuckets();
        b.Mark(100, NapReason.Background);
        b.Mark(100, NapReason.Minimized);

        Assert.True(b.Is(100, NapReason.Minimized));
        Assert.False(b.Is(100, NapReason.Background));
        Assert.Equal(1, b.Count);
    }

    [Fact]
    public void Clear_RemovesEverythingForThatPid_InOneCall()
    {
        // This is the bug class the unification kills: one Clear fully forgets the PID,
        // so no wake branch can leave it stranded in a bucket it forgot to scrub.
        var b = new NapBuckets();
        b.Mark(100, NapReason.Idle);

        Assert.True(b.Clear(100));
        Assert.False(b.IsNapped(100));
        Assert.Null(b.Get(100));
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void Clear_OnUnknownPid_ReturnsFalse_AndIsHarmless()
    {
        var b = new NapBuckets();
        Assert.False(b.Clear(999));   // no-op, never throws — safe to call defensively
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void Clear_DoesNotAffectOtherPids()
    {
        var b = new NapBuckets();
        b.Mark(1, NapReason.Minimized);
        b.Mark(2, NapReason.Tray);

        b.Clear(1);

        Assert.False(b.IsNapped(1));
        Assert.True(b.Is(2, NapReason.Tray));
        Assert.Equal(1, b.Count);
    }

    [Fact]
    public void Pids_ReflectsAllCategories()
    {
        var b = new NapBuckets();
        b.Mark(1, NapReason.Minimized);
        b.Mark(2, NapReason.Tray);
        b.Mark(3, NapReason.Background);
        b.Mark(4, NapReason.Idle);

        var pids = b.Pids.OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 4 }, pids);
    }

    [Fact]
    public void ClearAll_ForgetsEveryPid()
    {
        var b = new NapBuckets();
        b.Mark(1, NapReason.Minimized);
        b.Mark(2, NapReason.Idle);

        b.ClearAll();

        Assert.Equal(0, b.Count);
        Assert.Empty(b.Pids);
        Assert.False(b.IsNapped(1));
        Assert.False(b.IsNapped(2));
    }
}
