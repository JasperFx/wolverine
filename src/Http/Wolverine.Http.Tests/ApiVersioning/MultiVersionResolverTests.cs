using System.Reflection;
using Asp.Versioning;
using Shouldly;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

internal class MethodMultiVersionHandler
{
    [ApiVersion("1.0")]
    [ApiVersion("2.0")]
    public void Handle() { }
}

internal class MethodMultiVersionWithDeprecationHandler
{
    [ApiVersion("1.0", Deprecated = true)]
    [ApiVersion("2.0")]
    public void Handle() { }
}

[ApiVersion("1.0")]
[ApiVersion("2.0")]
internal class ClassMultiVersionHandler
{
    public void Handle() { }
}

[ApiVersion("1.0")]
[ApiVersion("2.0")]
[ApiVersion("3.0")]
internal class ClassMultiVersionWithMapToHandler
{
    [MapToApiVersion("2.0")]
    public void Handle() { }
}

[ApiVersion("1.0")]
internal class ClassWithMissingMapToHandler
{
    [MapToApiVersion("3.0")]
    public void Handle() { }
}

internal class MapToWithoutClassHandler
{
    [MapToApiVersion("1.0")]
    public void Handle() { }
}

[ApiVersion("1.0")]
internal class BothApiVersionAndMapToHandler
{
    [ApiVersion("2.0")]
    [MapToApiVersion("1.0")]
    public void Handle() { }
}

public class MultiVersionResolverTests
{
    private static MethodInfo MethodOf<T>(string name)
        => typeof(T).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!;

    [Fact]
    public void method_with_two_apiversion_attributes_returns_both_versions()
    {
        var method = MethodOf<MethodMultiVersionHandler>(nameof(MethodMultiVersionHandler.Handle));
        var versions = ApiVersionResolver.ResolveVersions(method);

        versions.Count.ShouldBe(2);
        versions.Select(r => r.Version).ShouldBe(new[] { new ApiVersion(1, 0), new ApiVersion(2, 0) });
        versions.All(r => !r.IsDeprecated).ShouldBeTrue();
    }

    [Fact]
    public void method_per_version_deprecation_is_applied_independently()
    {
        var method = MethodOf<MethodMultiVersionWithDeprecationHandler>(nameof(MethodMultiVersionWithDeprecationHandler.Handle));
        var versions = ApiVersionResolver.ResolveVersions(method).OrderBy(v => v.Version).ToList();

        versions.Count.ShouldBe(2);
        versions[0].Version.ShouldBe(new ApiVersion(1, 0));
        versions[0].IsDeprecated.ShouldBeTrue();
        versions[1].Version.ShouldBe(new ApiVersion(2, 0));
        versions[1].IsDeprecated.ShouldBeFalse();
    }

    [Fact]
    public void class_multi_version_method_returns_all_class_versions()
    {
        var method = MethodOf<ClassMultiVersionHandler>(nameof(ClassMultiVersionHandler.Handle));
        var versions = ApiVersionResolver.ResolveVersions(method);

        versions.Select(r => r.Version).ShouldBe(new[] { new ApiVersion(1, 0), new ApiVersion(2, 0) });
    }

    [Fact]
    public void mapto_filters_class_level_versions_to_listed_subset()
    {
        var method = MethodOf<ClassMultiVersionWithMapToHandler>(nameof(ClassMultiVersionWithMapToHandler.Handle));
        var versions = ApiVersionResolver.ResolveVersions(method);

        versions.Count.ShouldBe(1);
        versions[0].Version.ShouldBe(new ApiVersion(2, 0));
    }

    [Fact]
    public void mapto_listing_version_not_on_class_throws_naming_both()
    {
        var method = MethodOf<ClassWithMissingMapToHandler>(nameof(ClassWithMissingMapToHandler.Handle));

        var ex = Should.Throw<InvalidOperationException>(() => ApiVersionResolver.ResolveVersions(method));
        ex.Message.ShouldContain("MapToApiVersion");
        ex.Message.ShouldContain("ClassWithMissingMapToHandler");
        ex.Message.ShouldContain("3.0");
        ex.Message.ShouldContain("1.0");
    }

    [Fact]
    public void mapto_without_class_apiversion_throws()
    {
        var method = MethodOf<MapToWithoutClassHandler>(nameof(MapToWithoutClassHandler.Handle));

        var ex = Should.Throw<InvalidOperationException>(() => ApiVersionResolver.ResolveVersions(method));
        ex.Message.ShouldContain("MapToApiVersion");
        ex.Message.ShouldContain("MapToWithoutClassHandler");
    }

    [Fact]
    public void apiversion_and_mapto_on_same_method_throws()
    {
        var method = MethodOf<BothApiVersionAndMapToHandler>(nameof(BothApiVersionAndMapToHandler.Handle));

        var ex = Should.Throw<InvalidOperationException>(() => ApiVersionResolver.ResolveVersions(method));
        ex.Message.ShouldContain("[ApiVersion]");
        ex.Message.ShouldContain("[MapToApiVersion]");
    }
}
