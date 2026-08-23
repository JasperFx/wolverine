using Microsoft.Extensions.Hosting;

namespace Module1;

/// <summary>
/// GH-3778. Stands in for the very common test-harness shape where one assembly registers Wolverine
/// and a SHARED HELPER IN ANOTHER ASSEMBLY builds the host. IHostBuilder.UseWolverine() defers its
/// real work into a ConfigureServices callback that runs during Build(), so the caller's frame at
/// registration time is long gone by then — which is what made RegistrationCallingAssembly resolve
/// to whoever called Build() rather than to whoever called UseWolverine().
/// </summary>
public static class HostBuiltFromAnotherAssembly
{
    public static IHost Build(IHostBuilder builder) => builder.Build();
}
