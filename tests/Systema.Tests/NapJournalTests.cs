using System.Collections.Generic;
using System.Linq;
using Systema.Core;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Unit tests for <see cref="NapJournal"/> — the crash-recovery record of napped processes.
/// These lock in the on-disk format (so an older journal stays readable) and the PID-reuse
/// identity field that keeps recovery from touching the wrong process.
/// </summary>
public class NapJournalTests
{
    [Fact]
    public void FormatThenParse_RoundTrips()
    {
        var e = new NapJournal.Entry(1234, 133_000_000_000_000_000L, "firefox", 0x20 /* NORMAL */);
        Assert.True(NapJournal.TryParseLine(NapJournal.FormatLine(e), out var parsed));
        Assert.Equal(e.Pid, parsed.Pid);
        Assert.Equal(e.CreationTime, parsed.CreationTime);
        Assert.Equal(e.Name, parsed.Name);
        Assert.Equal(e.OriginalPriority, parsed.OriginalPriority);
    }

    [Fact]
    public void Parse_OlderJournalWithoutPriorityColumn_DefaultsToZero()
    {
        // Journals written before the priority column existed (3 fields) must still load,
        // with OriginalPriority = 0 (→ recovery falls back to Normal).
        Assert.True(NapJournal.TryParseLine("4321\t999\tchrome", out var parsed));
        Assert.Equal(4321, parsed.Pid);
        Assert.Equal("chrome", parsed.Name);
        Assert.Equal(0u, parsed.OriginalPriority);
    }

    [Fact]
    public void Parse_RejectsBlankAndMalformed()
    {
        Assert.False(NapJournal.TryParseLine(null, out _));
        Assert.False(NapJournal.TryParseLine("", out _));
        Assert.False(NapJournal.TryParseLine("   ", out _));
        Assert.False(NapJournal.TryParseLine("notanint\t123\tname", out _));
        Assert.False(NapJournal.TryParseLine("123\tnotalong\tname", out _));
        Assert.False(NapJournal.TryParseLine("123", out _));            // too few fields
    }

    [Fact]
    public void Format_SanitizesTabsInName_SoFieldsStayAligned()
    {
        // A name containing a tab/newline must not break the TSV layout.
        var e = new NapJournal.Entry(7, 42, "weird\tname\nhere", 0x80 /* HIGH */);
        string line = NapJournal.FormatLine(e);
        Assert.True(NapJournal.TryParseLine(line, out var parsed));
        Assert.Equal(7, parsed.Pid);
        Assert.Equal(42, parsed.CreationTime);
        Assert.DoesNotContain("\t", parsed.Name);
        Assert.DoesNotContain("\n", parsed.Name);
    }

    [Fact]
    public void Parse_PreservesCreationTime_ForPidReuseGuard()
    {
        // The creation time is the identity half — recovery compares it to reject a reused PID.
        var e = new NapJournal.Entry(9999, 8_675_309_000L, "app", 0x4000 /* BELOW_NORMAL */);
        NapJournal.TryParseLine(NapJournal.FormatLine(e), out var parsed);
        Assert.Equal(8_675_309_000L, parsed.CreationTime);
    }

    [Fact]
    public void MultiLine_ParsesEveryValidRecord_AndSkipsJunk()
    {
        var entries = new List<NapJournal.Entry>
        {
            new(100, 111, "a", 0x20),
            new(200, 222, "b", 0x80),
            new(300, 333, "c", 0),
        };
        // Simulate a journal file body, with a blank + malformed line mixed in.
        var lines = entries.Select(NapJournal.FormatLine).ToList();
        lines.Insert(1, "");
        lines.Add("garbage-line");

        var parsed = lines.Where(l => NapJournal.TryParseLine(l, out _))
                          .Select(l => { NapJournal.TryParseLine(l, out var e); return e; })
                          .OrderBy(x => x.Pid).ToList();

        Assert.Equal(3, parsed.Count);
        Assert.Equal(entries.Select(x => x.Pid), parsed.Select(x => x.Pid));
        Assert.Equal(entries.Select(x => x.Name), parsed.Select(x => x.Name));
    }
}
