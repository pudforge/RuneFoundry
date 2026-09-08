using System.Diagnostics;

namespace RuneFoundry.UI;

/// <summary>
/// Timing for the slow paths, off unless W2M_PERF is set. Costs a nullable check when off,
/// so the calls can stay in the code rather than being added back each time something
/// feels slow.
/// </summary>
public static class Perf
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("W2M_PERF") is { Length: > 0 } p
        ? (p == "1" ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "war2mod-perf.log") : p)
        : null;

    public static bool On => Path is not null;

    /// <summary>Names a piece of work to skip, so its cost can be measured by leaving it out.</summary>
    public static string Ablate { get; } = Environment.GetEnvironmentVariable("W2M_ABLATE") ?? "";

    public static bool Skip(string what) => Ablate.Contains(what, StringComparison.OrdinalIgnoreCase);

    public static IDisposable? Time(string label) => Path is null ? null : new Span(label);

    public static void Note(string line)
    {
        if (Path is null) return;
        try { System.IO.File.AppendAllText(Path, line + Environment.NewLine); } catch { }
    }

    private sealed class Span : IDisposable
    {
        private readonly string _label;
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        public Span(string label) => _label = label;

        public void Dispose() => Note($"{_label,-28} {_watch.Elapsed.TotalMilliseconds,8:F1} ms");
    }
}
