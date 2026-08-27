using System.Reflection;
using Shouldly;
using Wolverine.ClaimCheck.AmazonS3;

namespace Wolverine.AmazonS3.Tests;

/// <summary>
/// GH-4160. The claim check store moved from WolverineFx.ClaimCheck.AmazonS3 into WolverineFx.AmazonS3.
/// That package has shipped since 5.34.0, so it stays behind as an assembly carrying nothing but type
/// forwards — otherwise anything already compiled against it fails at load time with a
/// TypeLoadException rather than at build time with something a developer can act on.
/// </summary>
public class retired_claim_check_package
{
    // Copied next to the test assembly by the ProjectReference in the csproj, which is the only
    // reason this project references a package it never calls into.
    private static readonly Assembly theRetiredAssembly = Assembly.Load("Wolverine.ClaimCheck.AmazonS3");

    [Fact]
    public void the_types_now_live_in_the_amazon_s3_assembly()
    {
        typeof(AmazonS3ClaimCheckStore).Assembly.GetName().Name.ShouldBe("Wolverine.AmazonS3");
        typeof(AmazonS3ClaimCheckExtensions).Assembly.GetName().Name.ShouldBe("Wolverine.AmazonS3");
    }

    [Fact]
    public void the_retired_assembly_declares_no_types_of_its_own()
    {
        theRetiredAssembly.GetTypes().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Wolverine.ClaimCheck.AmazonS3.AmazonS3ClaimCheckStore")]
    [InlineData("Wolverine.ClaimCheck.AmazonS3.AmazonS3ClaimCheckExtensions")]
    public void an_old_reference_still_resolves_through_the_forward(string typeName)
    {
        // Exactly what the runtime does for an assembly compiled against the old package: ask the old
        // assembly for the type by its original name and follow the forward.
        var resolved = Type.GetType($"{typeName}, Wolverine.ClaimCheck.AmazonS3", throwOnError: true)!;

        resolved.Assembly.GetName().Name.ShouldBe("Wolverine.AmazonS3");

        theRetiredAssembly.GetForwardedTypes().ShouldContain(resolved);
    }

    [Fact]
    public void the_namespace_did_not_change_with_the_assembly()
    {
        // A source-breaking rename would defeat the point of keeping the shim at all: `using
        // Wolverine.ClaimCheck.AmazonS3;` has to keep compiling.
        typeof(AmazonS3ClaimCheckStore).Namespace.ShouldBe("Wolverine.ClaimCheck.AmazonS3");
        typeof(AmazonS3ClaimCheckExtensions).Namespace.ShouldBe("Wolverine.ClaimCheck.AmazonS3");
    }
}
