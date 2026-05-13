using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Wolverine.Runtime.Interop.MassTransit;

[Serializable]
internal class BusHostInfo
{
    public static readonly BusHostInfo Instance = new();

    public BusHostInfo()
    {
        FrameworkVersion = Environment.Version.ToString();
        OperatingSystemVersion = Environment.OSVersion.ToString();
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly() ??
                            System.Reflection.Assembly.GetCallingAssembly();
        MachineName = Environment.MachineName;
        MassTransitVersion = typeof(BusHostInfo).Assembly.GetName().Version?.ToString();

        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            ProcessId = currentProcess.Id;
            ProcessName = currentProcess.ProcessName;
            if ("dotnet".Equals(ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                ProcessName = GetUsefulProcessName(ProcessName);
            }
        }
        catch (PlatformNotSupportedException)
        {
            ProcessId = 0;
            ProcessName = GetUsefulProcessName("UWP");
        }

        var assemblyName = entryAssembly.GetName();
        Assembly = assemblyName.Name;
        AssemblyVersion = assemblyName.Version?.ToString() ?? "Unknown";
    }

    public string? MachineName { get; set; }
    public string? ProcessName { get; set; }
    public int ProcessId { get; set; }
    public string? Assembly { get; set; }
    public string? AssemblyVersion { get; set; }
    public string? FrameworkVersion { get; set; }
    public string? MassTransitVersion { get; set; }
    public string? OperatingSystemVersion { get; set; }

    // GetEntryAssembly()?.Location returns an empty string for single-file deployments
    // (IL3000). The IsNullOrWhiteSpace check below already handles that gracefully —
    // we fall back to the caller-supplied defaultProcessName (e.g. "dotnet" or "UWP").
    // The MassTransit interop layer is a best-effort host descriptor, so the empty-
    // string return is treated as "unknown app name" rather than an error condition.
    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "Empty-string return from Assembly.Location in single-file scenarios is handled by the IsNullOrWhiteSpace fallback below.")]
    private static string GetUsefulProcessName(string defaultProcessName)
    {
        var entryAssemblyLocation = System.Reflection.Assembly.GetEntryAssembly()?.Location;

        return string.IsNullOrWhiteSpace(entryAssemblyLocation)
            ? defaultProcessName
            : Path.GetFileNameWithoutExtension(entryAssemblyLocation);
    }
}