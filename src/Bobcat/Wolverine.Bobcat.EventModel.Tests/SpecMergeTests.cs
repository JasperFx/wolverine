using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Wolverine.Bobcat.EventModel.Tests;

/// <summary>
/// The additive re-run for specifications: without <c>--force</c>, an existing spec project gains the board's new
/// examples and nothing already there changes. The specification arm of Bobcat.EventModel's ImportMergeTests, which
/// moved here with <see cref="SpecMerge"/> and <see cref="SpecFileWriter"/> (GH-5082). Each test writes a smaller
/// board, then the full one, into the same directories, exactly as <c>write-specs</c> does.
/// </summary>
public sealed class SpecMergeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wolverine-spec-merge-").FullName;

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

    /// <summary>The Shelter board, plus a second event on Confirm appointment and a second example on Cancel appointment.</summary>
    private static readonly string Full = Boards.Shelter.Replace("\r", "")
        .Replace("      - e: Member / Appointment confirmed\n", "      - e: Member / Appointment confirmed\n      - e: Member / Reminder scheduled\n")
        + "\n      Cancelled before confirming:\n"
        + "        given:\n          - e: Home check appointment proposed\n"
        + "        when:\n          - c: Cancel appointment\n"
        + "        then:\n          - e: Appointment cancelled\n";

    private const string AcceptSteps = "      - c: Volunteer / Accept home check assignment\n      - e: Volunteer / Home check assignment accepted\n";

    private const string AcceptTest = "      Accepted:\n"
                                      + "        given:\n          - e: Home check requested\n          - e: Volunteer approved\n"
                                      + "        when:\n          - c: Accept home check assignment\n"
                                      + "        then:\n          - e: Home check assignment accepted\n";

    /// <summary>The full board less the Accept home check assignment slice and its example.</summary>
    private static string withoutAcceptSlice() => without(Full, AcceptSteps, AcceptTest);

    /// <summary>The full board less the second Cancel appointment example.</summary>
    private static string withoutSecondCancel() => Full[..Full.IndexOf("\n      Cancelled before confirming:", StringComparison.Ordinal)] + "\n";

    private static string without(string yaml, params string[] parts)
    {
        foreach (var part in parts)
        {
            var normalized = part.Replace("\r", "");
            yaml.ShouldContain(normalized);
            yaml = yaml.Replace(normalized, "");
        }

        return yaml;
    }

    /// <summary>What write-specs does: the stubs and definitions through Bobcat.EventModel's writer, then the specifications.</summary>
    private ImportFileWriter import(string yaml, bool force = false)
    {
        var board = EmlangReader.Read(yaml);
        var result = EmlangImport.ToCurated(board, "Shelter", "Shelter");
        var generatedSpecs = EmlangSpecWriter.Write(result.Board ?? board, result.Model, "Shelter", "Shelter.Specs", "marten");
        var generated = CSharpModelWriter.Write(result.Model, "Shelter", generatedSpecs.Additions, perChapter: true);

        var writer = new ImportFileWriter(force);
        writer.WriteStubs(app, generated.StubFiles);
        writer.WriteDefinitions(app, generated.DefinitionFiles, result.Model);
        new SpecFileWriter(writer, force).WriteSpecs(specs, generatedSpecs.Files);
        return writer;
    }

    private Dictionary<string, string> snapshot()
        => Directory.EnumerateFiles(_root, "*.cs", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllText);

    /// <summary>What <paramref name="after"/> holds beyond <paramref name="before"/>, failing unless the only change is one insertion.</summary>
    private static string insertedOnly(string before, string after)
    {
        var prefix = 0;
        while (prefix < before.Length && before[prefix] == after[prefix]) prefix++;
        var inserted = after.Length - before.Length;
        inserted.ShouldBeGreaterThan(0);
        after[(prefix + inserted)..].ShouldBe(before[prefix..], "everything after the insertion is byte-identical");
        return after.Substring(prefix, inserted);
    }

    [Fact]
    public void a_new_slices_specification_is_a_new_file_and_simply_written()
    {
        import(withoutAcceptSlice());
        File.Exists(Path.Combine(specs, "Volunteering", "AcceptHomeCheckAssignment.cs")).ShouldBeFalse();

        import(Full);

        File.Exists(Path.Combine(specs, "Volunteering", "AcceptHomeCheckAssignment.cs")).ShouldBeTrue();
    }

    [Fact]
    public void a_new_example_gets_its_specification_in_the_existing_class_and_the_existing_ones_are_untouched()
    {
        import(withoutSecondCancel());
        var path = Path.Combine(specs, "Booking", "CancelAppointment.cs");
        // The user edits the existing specification; the merge must not care
        var edited = File.ReadAllText(path).Replace("[Fact]", "[Fact] // checked by hand");
        File.WriteAllText(path, edited);

        var writer = import(Full);
        var inserted = insertedOnly(edited, File.ReadAllText(path));

        inserted.ShouldContain("public async Task cancelled_before_confirming()");
        inserted.ShouldNotContain("public async Task cancelled()");
        writer.Callouts.ShouldContain(x => x.Code == ImportCalloutCodes.MergedSpec && x.Slice == "CancelAppointment");

        var sources = Directory.EnumerateFiles(specs, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText);
        var model = EmlangImport.ToCurated(EmlangReader.Read(Full), "Shelter", "Shelter").Model;
        EmlangSpecWriter.MissingSpecs(model, sources).ShouldBeEmpty();
    }

    [Fact]
    public void a_specification_class_moved_to_another_file_is_still_where_the_new_example_goes()
    {
        import(withoutSecondCancel());
        var original = Path.Combine(specs, "Booking", "CancelAppointment.cs");
        var moved = Path.Combine(specs, "Booking", "Cancelling.cs");
        File.Move(original, moved);

        import(Full);

        File.Exists(original).ShouldBeFalse();
        File.ReadAllText(moved).ShouldContain("cancelled_before_confirming");
    }

    [Fact]
    public void a_global_using_the_stubs_now_need_is_added_and_the_rest_kept()
    {
        import(withoutAcceptSlice());
        var path = Path.Combine(specs, EmlangSpecWriter.GlobalUsingsFile);
        var trimmed = string.Join("\n", File.ReadAllLines(path).Where(x => !x.Contains("Shelter.Volunteering"))) + "\n";
        File.WriteAllText(path, trimmed);

        var writer = import(Full);

        File.ReadAllText(path).ShouldContain("global using Shelter.Volunteering;");
        writer.Callouts.ShouldContain(x => x.Code == ImportCalloutCodes.MergedUsing);
    }

    [Fact]
    public void the_test_support_file_is_never_merged_into()
    {
        import(withoutSecondCancel());
        var path = Path.Combine(specs, EmlangSpecWriter.TestSupportFile);
        File.WriteAllText(path, "// mine\n");

        var writer = import(Full);

        File.ReadAllText(path).ShouldBe("// mine\n");
        writer.Files.ShouldContain(x => x.Path == path && x.Mode == "kept");
    }

    [Fact]
    public void a_specification_file_that_does_not_parse_is_skipped_with_a_callout()
    {
        import(withoutSecondCancel());
        var path = Path.Combine(specs, "Booking", "CancelAppointment.cs");
        File.AppendAllText(path, "public class {\n");
        var before = File.ReadAllText(path);

        var writer = import(Full);

        File.ReadAllText(path).ShouldBe(before);
        writer.Callouts.ShouldContain(x => x.Code == ImportCalloutCodes.MergeSkippedUnparseable && x.File == path);
    }

    [Fact]
    public void writing_twice_changes_nothing_the_second_time()
    {
        import(withoutAcceptSlice());
        import(Full);
        var first = snapshot();

        var writer = import(Full);

        snapshot().ShouldBe(first);
        writer.Callouts.ShouldNotContain(x => x.Code.StartsWith("merged-"));
        writer.Files.ShouldAllBe(x => x.Mode == "kept");
        writer.Lines.ShouldBeEmpty();
    }

    [Fact]
    public void force_still_overwrites_every_specification_with_the_fresh_one()
    {
        import(withoutSecondCancel());
        var path = Path.Combine(specs, "Booking", "CancelAppointment.cs");
        File.WriteAllText(path, "// my edits\n");

        var writer = import(Full, force: true);

        File.ReadAllText(path).ShouldContain("cancelled_before_confirming");
        File.ReadAllText(path).ShouldNotContain("my edits");
        writer.Files.ShouldAllBe(x => x.Mode == "write");
    }
}
