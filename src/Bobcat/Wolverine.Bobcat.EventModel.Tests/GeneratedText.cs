using Bobcat.EventModel;

namespace Wolverine.Bobcat.EventModel.Tests;

/// <summary>Every generated file as one text, for the assertions that only care what was written, not where.</summary>
internal static class GeneratedText
{
    public static string AllCode(this GeneratedSpecs specs) => string.Join("\n", specs.Files.Select(x => x.Content));

    public static string AllStubs(this CSharpModelWriter.Output output) => string.Join("\n", output.StubFiles.Select(x => x.Content));
}
