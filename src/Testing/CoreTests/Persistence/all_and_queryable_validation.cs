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
