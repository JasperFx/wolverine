using System.Diagnostics;
using System.Reflection;

namespace Wolverine;

// TEMPORARY diagnostic for GH-3776. Not for merge.
internal static class AsmProbe
{
    private static readonly object _lock = new();
    private static readonly string _path =
        Path.Combine(Path.GetTempPath(), "wolverine-asm-probe.log");

    private static int _seq;

    public static void Write(string message)
    {
        try
        {
            lock (_lock)
            {
                var n = ++_seq;
                File.AppendAllText(_path,
                    $"[pid {Environment.ProcessId} #{n:D3}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // diagnostics must never fail a test
        }
    }

    public static string Name(Assembly? assembly) => assembly?.GetName().Name ?? "<null>";

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Temporary GH-3776 diagnostic; not for merge.")]
    public static string CallerStack()
    {
        var frames = new StackTrace(false).GetFrames();
        var interesting = frames
            .Select(f => f.GetMethod())
            .Where(m => m?.DeclaringType is not null)
            .Select(m => $"{m!.DeclaringType!.Assembly.GetName().Name}!{m.DeclaringType.FullName}.{m.Name}")
            .Where(x => !x.StartsWith("System") && !x.StartsWith("Microsoft"))
            .Take(25);

        return string.Join(" <- ", interesting);
    }
}
