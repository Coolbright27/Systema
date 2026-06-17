// ════════════════════════════════════════════════════════════════════════════
// NapJournal.cs  ·  Crash-recovery record of which processes Task Sleep has napped
// ════════════════════════════════════════════════════════════════════════════
//
// WHY THIS EXISTS
//   Task Sleep throttles background processes with OS-level changes that live ON
//   the target process, not inside Systema: Idle priority, lowest memory priority,
//   EcoQoS, GPU-idle scheduling, and E-core affinity. On a GRACEFUL shutdown
//   Systema restores them all. But if Systema dies WITHOUT running its shutdown
//   path — a hard crash, a force-quit (TerminateProcess), or a power loss — those
//   throttles persist, and the freshly-launched Systema has no in-memory record of
//   which processes it left throttled, so it never restores them. The user sees
//   apps stuck slow even after re-opening them.
//
//   This journal is that missing record. Each time the napped set changes, Systema
//   writes the current set here; on the next launch it reads the journal and undoes
//   the throttles on any process still alive (and still the SAME process — see the
//   creation-time field). On a clean shutdown the journal is cleared, so a normal
//   run never triggers recovery.
//
//   NOTE: the kernel Job-Object CPU cap is deliberately NOT something recovery can
//   undo — once the creating process dies, no other process can obtain a handle to
//   that job (its name dies with the handle), so the cap can't be lifted; it clears
//   only when the capped process exits. That case is handled by PREVENTION (restore
//   on every exit path Systema can run code on), not by this journal.
//
// FORMAT
//   One record per line, tab-separated:  pid \t creationTime100ns \t processName
//   creationTime100ns (the process creation FILETIME) is the PID-reuse guard: a PID
//   alone is not identity — by the next launch that number may belong to a totally
//   different process, which must NOT be touched.

using System;
using System.Collections.Generic;
using System.IO;

namespace Systema.Core;

public static class NapJournal
{
    /// <summary><c>%LOCALAPPDATA%\Systema\nap_journal.tsv</c> — per-user, NOT under OneDrive,
    /// so the frequent rewrites don't churn cloud sync.</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Systema", "nap_journal.tsv");

    /// <param name="OriginalPriority">The Win32 priority class the process had BEFORE Systema napped
    /// it, so recovery restores the real priority instead of guessing Normal. 0 = unknown → Normal.</param>
    public readonly record struct Entry(int Pid, long CreationTime, string Name, uint OriginalPriority);

    // ── Pure format / parse (unit-tested) ──────────────────────────────────────
    internal static string FormatLine(Entry e) =>
        $"{e.Pid}\t{e.CreationTime}\t{Sanitize(e.Name)}\t{e.OriginalPriority}";

    internal static bool TryParseLine(string? line, out Entry entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        string[] parts = line.Split('\t');
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], out int pid)) return false;
        if (!long.TryParse(parts[1], out long ct)) return false;
        // 4th column (original priority) is optional — older journals omit it; 0 = unknown → Normal.
        uint origPrio = 0;
        if (parts.Length >= 4) uint.TryParse(parts[3], out origPrio);
        entry = new Entry(pid, ct, parts[2], origPrio);
        return true;
    }

    private static string Sanitize(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    // ── File I/O (best-effort; never throws) ───────────────────────────────────
    public static void Save(IEnumerable<Entry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var lines = new List<string>();
            foreach (var e in entries) lines.Add(FormatLine(e));
            File.WriteAllLines(FilePath, lines);
        }
        catch { /* journal is best-effort — losing it only costs crash recovery, never correctness */ }
    }

    public static IReadOnlyList<Entry> Load()
    {
        var result = new List<Entry>();
        try
        {
            if (!File.Exists(FilePath)) return result;
            foreach (string line in File.ReadAllLines(FilePath))
                if (TryParseLine(line, out Entry e)) result.Add(e);
        }
        catch { /* corrupt / locked journal — treat as empty */ }
        return result;
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* ignore */ }
    }
}
