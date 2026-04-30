using Shouldly;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Configuration;

public class composite_wolverine_typeloader_tests
{
    [Fact]
    public void unions_handler_types_across_inner_loaders_and_dedupes()
    {
        var a = new StubLoader(handlers: [typeof(string), typeof(int)]);
        var b = new StubLoader(handlers: [typeof(int), typeof(double)]);

        var composite = new CompositeWolverineTypeLoader(new[] { a, b });

        composite.DiscoveredHandlerTypes
            .ShouldBe(new[] { typeof(string), typeof(int), typeof(double) }, ignoreOrder: true);
    }

    [Fact]
    public void unions_message_types_and_dedupes_by_message_type()
    {
        var a = new StubLoader(messages: [(typeof(string), "alias-a"), (typeof(int), "alias-int-a")]);
        var b = new StubLoader(messages: [(typeof(int), "alias-int-b"), (typeof(double), "alias-d")]);

        var composite = new CompositeWolverineTypeLoader(new[] { a, b });

        composite.DiscoveredMessageTypes.Select(t => t.MessageType).ShouldBe(
            new[] { typeof(string), typeof(int), typeof(double) }, ignoreOrder: true);

        // First loader wins on alias collision — matches single-loader semantics.
        composite.DiscoveredMessageTypes
            .Single(t => t.MessageType == typeof(int)).Alias
            .ShouldBe("alias-int-a");
    }

    [Fact]
    public void pre_generated_handler_types_unioned_when_any_inner_has_them()
    {
        var a = new StubLoader(preGenerated: new Dictionary<string, Type> { ["A"] = typeof(string) });
        var b = new StubLoader(preGenerated: new Dictionary<string, Type> { ["B"] = typeof(int) });

        var composite = new CompositeWolverineTypeLoader(new[] { a, b });

        composite.HasPreGeneratedHandlers.ShouldBeTrue();
        composite.PreGeneratedHandlerTypes.ShouldNotBeNull();
        composite.PreGeneratedHandlerTypes!["A"].ShouldBe(typeof(string));
        composite.PreGeneratedHandlerTypes!["B"].ShouldBe(typeof(int));

        composite.TryFindPreGeneratedType("A").ShouldBe(typeof(string));
        composite.TryFindPreGeneratedType("B").ShouldBe(typeof(int));
        composite.TryFindPreGeneratedType("missing").ShouldBeNull();
    }

    [Fact]
    public void pre_generated_handler_types_are_null_when_no_inner_has_them()
    {
        var a = new StubLoader();
        var b = new StubLoader();

        var composite = new CompositeWolverineTypeLoader(new[] { a, b });

        composite.HasPreGeneratedHandlers.ShouldBeFalse();
        composite.PreGeneratedHandlerTypes.ShouldBeNull();
    }

    [Fact]
    public void empty_inner_list_throws()
    {
        Should.Throw<ArgumentException>(() => new CompositeWolverineTypeLoader(Array.Empty<IWolverineTypeLoader>()));
    }

    private sealed class StubLoader : IWolverineTypeLoader
    {
        public StubLoader(
            IReadOnlyList<Type>? handlers = null,
            IReadOnlyList<(Type, string)>? messages = null,
            IReadOnlyDictionary<string, Type>? preGenerated = null)
        {
            DiscoveredHandlerTypes = handlers ?? Array.Empty<Type>();
            DiscoveredMessageTypes = messages ?? Array.Empty<(Type, string)>();
            PreGeneratedHandlerTypes = preGenerated;
            HasPreGeneratedHandlers = preGenerated is { Count: > 0 };
        }

        public IReadOnlyList<Type> DiscoveredHandlerTypes { get; }
        public IReadOnlyList<(Type MessageType, string Alias)> DiscoveredMessageTypes { get; }
        public IReadOnlyList<Type> DiscoveredHttpEndpointTypes { get; } = Array.Empty<Type>();
        public IReadOnlyList<Type> DiscoveredExtensionTypes { get; } = Array.Empty<Type>();
        public bool HasPreGeneratedHandlers { get; }
        public IReadOnlyDictionary<string, Type>? PreGeneratedHandlerTypes { get; }

        public Type? TryFindPreGeneratedType(string typeName) =>
            PreGeneratedHandlerTypes is not null && PreGeneratedHandlerTypes.TryGetValue(typeName, out var t) ? t : null;
    }
}
