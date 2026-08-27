using System.Runtime.CompilerServices;
using Wolverine.ClaimCheck.AmazonS3;

// GH-4160. The Amazon S3 claim check store moved into WolverineFx.AmazonS3, which owns every S3
// concern -- the claim check store, entity persistence and saga storage -- rather than one package
// per concern against the same service.
//
// This assembly is deliberately left behind carrying nothing but these forwards, because
// WolverineFx.ClaimCheck.AmazonS3 has shipped since 5.34.0 and an assembly that simply vanishes
// breaks every consumer already compiled against it at runtime, not at build time. The types keep
// their original namespace for the same reason: source that reads
// `using Wolverine.ClaimCheck.AmazonS3;` still compiles unchanged.
//
// Both packages ship at the same version out of Directory.Build.props, so the dependency is always
// exact. Deprecate the package on nuget.org rather than deleting this project.
[assembly: TypeForwardedTo(typeof(AmazonS3ClaimCheckStore))]
[assembly: TypeForwardedTo(typeof(AmazonS3ClaimCheckExtensions))]
