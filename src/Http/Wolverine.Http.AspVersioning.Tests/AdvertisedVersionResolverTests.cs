using System.Reflection;
using Asp.Versioning;
using Shouldly;

namespace Wolverine.Http.AspVersioning.Tests;

internal class NoAdvertisedHandler
{
    public void Handle() { }
}

[AdvertiseApiVersions("1.0")]
internal class ClassOnlyAdvertisedHandler
{
    public void Handle() { }
}

internal class MethodOnlyAdvertisedHandler
{
    [AdvertiseApiVersions("2.0")]
    public void Handle() { }
}

[AdvertiseApiVersions("4.0", "5.0")]
internal class MultipleAdvertisedHandler
{
    [AdvertiseApiVersions("1.0", "2.0")]
    [AdvertiseApiVersions("3.0")]
    public void Handle() { }
}

[AdvertiseApiVersions("1.0", Deprecated = true)]
[AdvertiseApiVersions("2.0")]
[AdvertiseApiVersions("4.0", Deprecated = true)]
internal class DeprecatedAdvertisedHandler
{
    [AdvertiseApiVersions("1.0")]
    [AdvertiseApiVersions("2.0", Deprecated = true)]
    [AdvertiseApiVersions("3.0")]
    public void Handle() { }
}

public class AdvertisedVersionResolverTests
{
    private static MethodInfo MethodOf<T>(string name = "Handle") =>
        typeof(T).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!;

    [Fact]
    public void no_attribute_returns_empty()
    {
        var method = MethodOf<NoAdvertisedHandler>(nameof(NoAdvertisedHandler.Handle));
        AdvertisedVersionResolver.ResolveAdvertised(method).ShouldBeEmpty();
    }

    [Fact]
    public void class_attribute_resolves()
    {
        var method = MethodOf<ClassOnlyAdvertisedHandler>(
            nameof(ClassOnlyAdvertisedHandler.Handle)
        );
        var result = AdvertisedVersionResolver.ResolveAdvertised(method);
        result.ShouldHaveSingleItem();
        result[0].Version.ShouldBe(new ApiVersion(1, 0));
        result[0].IsDeprecated.ShouldBeFalse();
    }

    [Fact]
    public void method_attribute_resolves()
    {
        var method = MethodOf<MethodOnlyAdvertisedHandler>(
            nameof(MethodOnlyAdvertisedHandler.Handle)
        );
        var result = AdvertisedVersionResolver.ResolveAdvertised(method);
        result.ShouldHaveSingleItem();
        result[0].Version.ShouldBe(new ApiVersion(2, 0));
        result[0].IsDeprecated.ShouldBeFalse();
    }

    [Fact]
    public void multiple_attributes_merge()
    {
        var method = MethodOf<MultipleAdvertisedHandler>(nameof(MultipleAdvertisedHandler.Handle));
        var result = AdvertisedVersionResolver.ResolveAdvertised(method);
        result
            .Select(r => r.Version)
            .ShouldBe(Enumerable.Range(1, 5).Select(i => new ApiVersion(i, 0)));
    }

    [Fact]
    public void deprecations_merge_and_propagate()
    {
        var method = MethodOf<DeprecatedAdvertisedHandler>(
            nameof(DeprecatedAdvertisedHandler.Handle)
        );
        var result = AdvertisedVersionResolver.ResolveAdvertised(method);
        result
            .Select(r => r.Version)
            .ShouldBe(Enumerable.Range(1, 4).Select(i => new ApiVersion(i, 0)));
        result.Single(r => r.Version == new ApiVersion(1, 0)).IsDeprecated.ShouldBeTrue();
        result.Single(r => r.Version == new ApiVersion(2, 0)).IsDeprecated.ShouldBeTrue();
        result.Single(r => r.Version == new ApiVersion(3, 0)).IsDeprecated.ShouldBeFalse();
        result.Single(r => r.Version == new ApiVersion(4, 0)).IsDeprecated.ShouldBeTrue();
    }
}
