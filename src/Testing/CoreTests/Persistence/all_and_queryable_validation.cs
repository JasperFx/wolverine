using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine;
using Wolverine.Persistence;
using Xunit;

namespace CoreTests.Persistence;

// [All] and [Queryable] are strict about the parameter type they will accept. The point of these is that the
// message says what was wrong and what to write instead, rather than failing somewhere in codegen.
public class all_and_queryable_validation
{
    // The type check lives in the parameter attribute's Modify(), which runs during code generation for
    // the chain rather than at host startup -- the same timing [Entity]'s own errors have. So the message
    // surfaces on first invocation, which is what these assert.
    private static async Task<InvalidOperationException> shouldFail(Type handlerType)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery().IncludeType(handlerType))
            .StartAsync();

        return await Should.ThrowAsync<InvalidOperationException>(
            () => host.InvokeAsync(new CountValidationColors()));
    }

    [Fact]
    public async Task all_rejects_a_bare_ienumerable()
    {
        var ex = await shouldFail(typeof(AllOnEnumerableHandler));

        ex.Message.ShouldContain("[All] attribute can only be applied to a parameter of type IReadOnlyList<T>");
        ex.Message.ShouldContain("colors");                 // names the parameter
        ex.Message.ShouldContain(nameof(AllOnEnumerableHandler)); // names the declaring method
        ex.Message.ShouldContain("IReadOnlyList<ValidationColor>"); // suggests the concrete fix
    }

    [Fact]
    public async Task all_rejects_a_single_entity()
    {
        var ex = await shouldFail(typeof(AllOnSingleHandler));

        ex.Message.ShouldContain("IReadOnlyList<T>");
    }

    [Fact]
    public async Task queryable_rejects_a_non_queryable_parameter()
    {
        var ex = await shouldFail(typeof(QueryableOnListHandler));

        ex.Message.ShouldContain("[Queryable] attribute can only be applied to a parameter of type IQueryable<T>");
        ex.Message.ShouldContain("colors");
        ex.Message.ShouldContain("IQueryable<ValidationColor>");
    }
}

// GH-3937: the provider-resolution failures used to name only the parameter and its element type. These
// attributes validate at CODEGEN, so the failure can land on a chain the developer did not know was being
// compiled -- an assembly carrying [WolverineModule] puts every endpoint in it into discovery, and a slim
// storeless test host then fails at bootstrap over an endpoint it never asked for. The declaring method is
// the only thread in the message back to a type they recognise.
public class provider_failures_name_the_declaring_method
{
    // No persistence is registered, so the resolved provider is InMemoryPersistenceFrameProvider, whose
    // TryBuild*Frame methods take IPersistenceFrameProvider's default implementations and return false.
    private static async Task<InvalidOperationException> shouldFailOnAStorelessHost(Type handlerType)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery().IncludeType(handlerType))
            .StartAsync();

        return await Should.ThrowAsync<InvalidOperationException>(
            () => host.InvokeAsync(new CountValidationColors()));
    }

    [Fact]
    public async Task all_names_the_declaring_method()
    {
        var ex = await shouldFailOnAStorelessHost(typeof(AllWithNoStoreHandler));

        ex.Message.ShouldContain("does not support [All]");
        ex.Message.ShouldContain("colors");
        ex.Message.ShouldContain($"{typeof(AllWithNoStoreHandler).FullNameInCode()}.Handle()");
    }

    [Fact]
    public async Task queryable_names_the_declaring_method()
    {
        var ex = await shouldFailOnAStorelessHost(typeof(QueryableWithNoStoreHandler));

        ex.Message.ShouldContain("does not support [Queryable]");
        ex.Message.ShouldContain("colors");
        ex.Message.ShouldContain($"{typeof(QueryableWithNoStoreHandler).FullNameInCode()}.Handle()");
    }

    [Fact]
    public async Task first_or_default_names_the_declaring_method()
    {
        var ex = await shouldFailOnAStorelessHost(typeof(FirstOrDefaultWithNoStoreHandler));

        ex.Message.ShouldContain("does not support [FirstOrDefault]");
        ex.Message.ShouldContain("color");
        ex.Message.ShouldContain($"{typeof(FirstOrDefaultWithNoStoreHandler).FullNameInCode()}.Handle()");
    }
}

public class ValidationColor
{
    public Guid Id { get; set; }
}

public record CountValidationColors;

// [WolverineIgnore] -- these are deliberately invalid and would break every other host in this assembly
[WolverineIgnore]
public static class AllOnEnumerableHandler
{
    public static void Handle(CountValidationColors command, [All] IEnumerable<ValidationColor> colors) { }
}

[WolverineIgnore]
public static class AllOnSingleHandler
{
    public static void Handle(CountValidationColors command, [All] ValidationColor colors) { }
}

[WolverineIgnore]
public static class QueryableOnListHandler
{
    public static void Handle(CountValidationColors command, [Queryable] IReadOnlyList<ValidationColor> colors) { }
}

// Correctly typed, but no store is registered -- these reach the provider-resolution throws rather than the
// parameter type checks above.
[WolverineIgnore]
public static class AllWithNoStoreHandler
{
    public static void Handle(CountValidationColors command, [All] IReadOnlyList<ValidationColor> colors) { }
}

[WolverineIgnore]
public static class QueryableWithNoStoreHandler
{
    public static void Handle(CountValidationColors command, [Queryable] IQueryable<ValidationColor> colors) { }
}

[WolverineIgnore]
public static class FirstOrDefaultWithNoStoreHandler
{
    public static void Handle(CountValidationColors command, [FirstOrDefault] ValidationColor? color) { }
}
