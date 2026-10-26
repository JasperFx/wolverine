using System.Text.Json;
using Bobcat.EventModel;
using Shouldly;

namespace Wolverine.Bobcat.EventModel.Tests;

/// <summary>
/// GH-5082: <c>write-specs</c> end to end, the command that took over <c>bobcat import-event-model --specs</c>: the board in,
/// the stubs merged, the specifications and the spec project written, and a second run additive.
/// </summary>
public sealed class WriteSpecsCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wolverine-write-specs-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // the temp folder is the OS's to clear
        }
    }

    private string app => Path.Combine(_root, "Shelter");
    private string specs => Path.Combine(_root, "Shelter.Specs");

    private async Task<bool> run(params string[] extra)
    {
        var board = Path.Combine(_root, "shelter.yaml");
        if (!File.Exists(board)) await File.WriteAllTextAsync(board, Boards.Shelter);

        Directory.CreateDirectory(app);
        var project = Path.Combine(app, "Shelter.csproj");
        if (!File.Exists(project)) await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />\n");

        var input = new WriteSpecsInput
        {
            FilePath = board,
            ModelFlag = "Shelter",
            OutFlag = app,
            StoreFlag = "marten",
            JsonFlag = extra.Contains("--json")
        };

        return await new WriteSpecsCommand().Execute(input);
    }

    [Fact]
    public async Task writes_the_stubs_the_specifications_and_the_spec_project()
    {
        (await run()).ShouldBeTrue();

        File.Exists(Path.Combine(app, "Features", "Booking", "ConfirmAppointment.cs")).ShouldBeTrue();
        File.Exists(Path.Combine(specs, EmlangSpecWriter.TestSupportFile)).ShouldBeTrue();
        File.ReadAllText(Path.Combine(specs, "Booking", "ConfirmAppointment.cs")).ShouldContain("[BobcatFeature(\"ConfirmAppointment\")]");

        var csproj = File.ReadAllText(Path.Combine(specs, "Shelter.Specs.csproj"));
        csproj.ShouldContain($"<BobcatVersion Condition=\"'$(BobcatVersion)' == ''\">{SpecProjectWriter.BobcatVersion}</BobcatVersion>");
        csproj.ShouldContain($"<WolverineFxBobcatVersion Condition=\"'$(WolverineFxBobcatVersion)' == ''\">{SpecProjectWriter.DefaultWolverineFxBobcatVersion}</WolverineFxBobcatVersion>");
        csproj.ShouldContain(@"<ProjectReference Include=""..\Shelter\Shelter.csproj"" />");
    }

    [Fact]
    public async Task a_second_run_changes_nothing()
    {
        (await run()).ShouldBeTrue();
        var first = Directory.EnumerateFiles(_root, "*.*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllText);

        (await run()).ShouldBeTrue();

        Directory.EnumerateFiles(_root, "*.*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllText).ShouldBe(first);
    }

    [Fact]
    public async Task an_input_that_is_not_a_board_is_refused()
    {
        var path = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(path, "just some notes");

        (await new WriteSpecsCommand().Execute(new WriteSpecsInput { FilePath = path, OutFlag = app })).ShouldBeFalse();
        Directory.Exists(specs).ShouldBeFalse();
    }

    [Fact]
    public async Task a_missing_file_is_refused()
    {
        (await new WriteSpecsCommand().Execute(new WriteSpecsInput { FilePath = Path.Combine(_root, "nope.json") })).ShouldBeFalse();
    }
}
