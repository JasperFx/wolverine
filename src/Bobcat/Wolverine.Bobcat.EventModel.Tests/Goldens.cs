using System.Runtime.CompilerServices;
using Shouldly;

namespace Wolverine.Bobcat.EventModel.Tests;

/// <summary>
/// The generated specifications for a whole fixture, checked against a file under <c>Goldens/</c> (GH-6004). Set
/// <c>WOLVERINE_UPDATE_SPEC_GOLDENS=1</c> to rewrite the files from the current writer, then read the diff.
/// </summary>
internal static class Goldens
{
    private static readonly string Directory = Path.Combine(sourceDirectory(), "Goldens");

    public static void Check(string name, GeneratedSpecs specs)
    {
        var text = string.Join("\n", specs.Files.OrderBy(x => x.Path, StringComparer.Ordinal)
            .Select(x => $"// ===== {x.Path} =====\n{x.Content.TrimEnd('\n', '\r')}\n"));

        Check(name, text);
    }

    public static void Check(string name, string text)
    {
        var path = Path.Combine(Directory, name + ".txt");
        text = text.Replace("\r\n", "\n");

        if (Environment.GetEnvironmentVariable("WOLVERINE_UPDATE_SPEC_GOLDENS") == "1")
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(path, text);
            return;
        }

        File.Exists(path).ShouldBeTrue($"No golden file at {path}. Run once with WOLVERINE_UPDATE_SPEC_GOLDENS=1 to write it.");
        File.ReadAllText(path).Replace("\r\n", "\n").ShouldBe(text,
            $"The generated specifications differ from {path}. If the change is intended, rerun with WOLVERINE_UPDATE_SPEC_GOLDENS=1 and read the diff.");
    }

    private static string sourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
