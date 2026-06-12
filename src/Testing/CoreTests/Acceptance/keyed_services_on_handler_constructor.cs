using Lamar.Microsoft.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Attributes;
using Xunit;

namespace CoreTests.Acceptance;

// GH-3081: keyed services injected through a handler's CONSTRUCTOR were resolved by type only,
// dropping the [FromKeyedServices] key — so two keyed parameters of the same service type both
// resolved to the default (last) registration. Fixed in JasperFx 2.9.10: ConstructorFrame now
// resolves constructor parameters via the attribute-aware FindVariable(ParameterInfo) overload.
//
// Companion to using_with_keyed_services, which covers keyed services on Handle METHOD parameters
// (that path always worked because MethodCall already used the attribute-aware overload).
//
// Covered against both the default container and Lamar (the issue was reported on Lamar). The fix
// is in the shared codegen layer, so both containers must resolve the keyed constructor parameters
// correctly.
public class keyed_services_on_handler_constructor
{
    [Fact]
    public Task each_keyed_constructor_parameter_resolves_to_its_own_registration()
    {
        return assertKeyedConstructorInjectionResolvesByKey(Host.CreateDefaultBuilder());
    }

    [Fact]
    public Task each_keyed_constructor_parameter_resolves_to_its_own_registration_on_lamar()
    {
        return assertKeyedConstructorInjectionResolvesByKey(Host.CreateDefaultBuilder().UseLamar());
    }

    private static async Task assertKeyedConstructorInjectionResolvesByKey(IHostBuilder builder)
    {
        using var host = await builder
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<KeyedCtorHandler>();

                opts.Services.AddKeyedScoped<IKeyedRepo, FirstRepo>("Test");
                opts.Services.AddKeyedScoped<IKeyedRepo, SecondRepo>("Test2");
            }).StartAsync();

        KeyedCtorRecording.Seen.Clear();

        await host.InvokeAsync(new KeyedCtorCommand());

        // first ctor param -> "Test" -> FirstRepo; second -> "Test2" -> SecondRepo.
        // Before the fix both parameters resolved to the last registration (SecondRepo).
        KeyedCtorRecording.Seen.ShouldBe(["First", "Second"]);
    }
}

public interface IKeyedRepo
{
    string Name { get; }
}

public class FirstRepo : IKeyedRepo
{
    public string Name => "First";
}

public class SecondRepo : IKeyedRepo
{
    public string Name => "Second";
}

public static class KeyedCtorRecording
{
    public static readonly List<string> Seen = new();
}

public record KeyedCtorCommand;

[WolverineIgnore]
public class KeyedCtorHandler(
    [FromKeyedServices("Test")] IKeyedRepo first,
    [FromKeyedServices("Test2")] IKeyedRepo second)
{
    public void Handle(KeyedCtorCommand command)
    {
        KeyedCtorRecording.Seen.Add(first.Name);
        KeyedCtorRecording.Seen.Add(second.Name);
    }
}
