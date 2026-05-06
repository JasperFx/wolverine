using System.Reflection;
using Asp.Versioning;
using Shouldly;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

internal class NoVersionHandler { public void Handle() { } }

[ApiVersion("2.0")]
internal class ClassOnlyVersionHandler { public void Handle() { } }

internal class MethodOnlyVersionHandler { [ApiVersion("1.0")] public void Handle() { } }

[ApiVersion("2.0")]
internal class MethodOverridesClassHandler { [ApiVersion("1.0")] public void Handle() { } }

internal class MultipleVersionsOnMethodHandler
{
    [ApiVersion("1.0")]
    [ApiVersion("2.0")]
    public void Handle() { }
}

internal class DeprecatedMethodHandler
{
    [ApiVersion("1.0", Deprecated = true)]
    public void Handle() { }
}

public class ApiVersionResolverTests
{
    private static MethodInfo MethodOf<T>(string name)
        => typeof(T).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!;

    [Fact]
    public void no_attribute_returns_empty()
    {
        var method = MethodOf<NoVersionHandler>(nameof(NoVersionHandler.Handle));
        ApiVersionResolver.ResolveVersions(method).ShouldBeEmpty();
    }

    [Fact]
    public void class_only_attribute_resolves_to_class_version()
    {
        var method = MethodOf<ClassOnlyVersionHandler>(nameof(ClassOnlyVersionHandler.Handle));
        var result = ApiVersionResolver.ResolveVersions(method);
        result.ShouldHaveSingleItem();
        result[0].Version.ShouldBe(new ApiVersion(2, 0));
        result[0].IsDeprecated.ShouldBeFalse();
    }

    [Fact]
    public void method_attribute_resolves_to_method_version()
    {
        var method = MethodOf<MethodOnlyVersionHandler>(nameof(MethodOnlyVersionHandler.Handle));
        var result = ApiVersionResolver.ResolveVersions(method);
        result.ShouldHaveSingleItem();
        result[0].Version.ShouldBe(new ApiVersion(1, 0));
        result[0].IsDeprecated.ShouldBeFalse();
    }

    [Fact]
    public void method_attribute_overrides_class_attribute()
    {
        var method = MethodOf<MethodOverridesClassHandler>(nameof(MethodOverridesClassHandler.Handle));
        var result = ApiVersionResolver.ResolveVersions(method);
        result.ShouldHaveSingleItem();
        result[0].Version.ShouldBe(new ApiVersion(1, 0));
        result[0].IsDeprecated.ShouldBeFalse();
    }

    [Fact]
    public void multiple_versions_on_same_method_returns_all_of_them()
    {
        var method = MethodOf<MultipleVersionsOnMethodHandler>(nameof(MultipleVersionsOnMethodHandler.Handle));
        var result = ApiVersionResolver.ResolveVersions(method);
        result.Select(r => r.Version).ShouldBe(new[] { new ApiVersion(1, 0), new ApiVersion(2, 0) });
    }

    [Fact]
    public void deprecated_attribute_flag_is_propagated()
    {
        var result = ApiVersionResolver.ResolveVersions(MethodOf<DeprecatedMethodHandler>(nameof(DeprecatedMethodHandler.Handle)));
        result.ShouldHaveSingleItem();
        result[0].Version.ShouldBe(new ApiVersion(1, 0));
        result[0].IsDeprecated.ShouldBeTrue();
    }
}
