using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.EventSourcing.Codegen;

/// <summary>
///     An HTTP endpoint's optimistic concurrency check on the expected version the request supplies, placed after the
///     missing aggregate handling so that a stream that does not exist answers 404 rather than a concurrency failure.
///     See <see cref="ExpectedStreamVersion" />.
/// </summary>
internal class ExpectedVersionCheckFrame : SyncFrame
{
    private readonly Variable _stream;
    private readonly Variable _version;

    public ExpectedVersionCheckFrame(Variable stream, Variable version)
    {
        _stream = stream;
        _version = version;

        uses.Add(stream);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // The version can be a member of the message read by a frame nothing else in the chain depends on
        yield return _version;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine(
            $"{typeof(ExpectedStreamVersion).FullNameInCode()}.{nameof(ExpectedStreamVersion.AssertStartingVersion)}({_stream.Usage}, {_version.Usage});");

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine(
            $"{typeof(ExpectedStreamVersion).FSharpName()}.{nameof(ExpectedStreamVersion.AssertStartingVersion)}({_stream.FSharpUsage}, {_version.FSharpUsage})");

        Next?.GenerateFSharpCode(method, writer);
    }
}
