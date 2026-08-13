using System.Runtime.CompilerServices;

namespace IntegrationTests;

/// <summary>
///     Turns off the host builder's configuration file watching for the whole test process.
/// </summary>
/// <remarks>
///     <para>
///         <c>Host.CreateDefaultBuilder()</c> registers <c>appsettings.json</c> with <c>reloadOnChange: true</c>,
///         and that stands up a <c>PhysicalFileProvider</c> and a <c>FileSystemWatcher</c> over the content root
///         <b>whether or not the file exists</b>. A test assembly builds hundreds of hosts, and the watchers
///         outlive them: a full MartenTests run was holding <b>1028</b> kqueue handles by test ~594, against
///         <b>4</b> with this switch on.
///     </para>
///     <para>
///         Nothing in this repository reloads configuration mid-test, so those watchers are pure overhead —
///         and on macOS the failure mode is ugly. Once the watcher can no longer start, its change token fires
///         immediately, re-registers, and fires again, recursing until the stack overflows and the process dies
///         on SIGABRT (exit 134) partway through a run.
///     </para>
///     <para>
///         <c>hostBuilder:reloadConfigOnChange</c> is the framework's own opt-out. It is read out of <i>host</i>
///         configuration, which includes <c>DOTNET_</c>-prefixed environment variables, so setting it here
///         reaches every <c>CreateDefaultBuilder</c>, <c>CreateApplicationBuilder</c> and
///         <c>WebApplication.CreateBuilder</c> the process goes on to build. The environment variable provider
///         translates <c>__</c> to <c>:</c>, which is the portable spelling of the key.
///     </para>
/// </remarks>
internal static class DisableConfigReloadInTests
{
    private const string Key = "DOTNET_hostBuilder__reloadConfigOnChange";

    /// <summary>
    ///     Runs before the test assembly's entry point, and so before any host is built.
    /// </summary>
    [ModuleInitializer]
    internal static void Disable()
    {
        // Deliberately does not overwrite: a job that wants the watchers back can export its own value.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Key)))
        {
            Environment.SetEnvironmentVariable(Key, "false");
        }
    }
}
