using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests;

/// <summary>
/// Coverage for <see cref="WolverineOptions.RestoreV6Defaults"/>, the single opt-out for
/// every default Wolverine 7.0 flips (GH-5191).
///
/// The migration guide's "Defaults that changed in 7.0" table is the list of flips. Every
/// row names, in its "Restore test" column, the test class proving that RestoreV6Defaults()
/// puts the 6.x value back. A row naming this class must have an entry in
/// <see cref="CoreRestorations"/>; a row naming another class (an extension package's
/// default) must point at a class that exists. A flip that adds a row cannot forget its
/// restore test, and a core restore entry cannot exist without a row in the guide.
/// </summary>
public class restore_v6_defaults_tests
{
    private const string ThisTestClass = nameof(restore_v6_defaults_tests);

    /// <summary>
    /// One entry per core default flipped in 7.0, keyed by the "Setting" cell of its row in
    /// the migration guide's 7.0 defaults table. Each entry asserts the 7.0 value on a fresh
    /// <see cref="WolverineOptions"/>, then the 6.x value after RestoreV6Defaults().
    /// </summary>
    private static readonly Dictionary<string, (Action<WolverineOptions> AssertV7Default, Action<WolverineOptions> AssertV6Value)>
        CoreRestorations = new()
        {
            ["`DurabilitySettings.MessageDeduplicationMode`"] = (
                o => o.Durability.MessageDeduplicationMode.ShouldBe(MessageDeduplicationMode.CompareByHash),
                o => o.Durability.MessageDeduplicationMode.ShouldBe(MessageDeduplicationMode.None)),
            ["`DurabilitySettings.EnableDeduplicatedResponses`"] = (
                o => o.Durability.EnableDeduplicatedResponses.ShouldBeTrue(),
                o => o.Durability.EnableDeduplicatedResponses.ShouldBeFalse()),
            // GH-5193
            ["`DurabilitySettings.AlwaysProvisionOptionalSchemaObjects`"] = (
                o => o.Durability.AlwaysProvisionOptionalSchemaObjects.ShouldBeTrue(),
                o => o.Durability.AlwaysProvisionOptionalSchemaObjects.ShouldBeFalse()),
            // GH-5197. With no message store preference; SQL Server-only applications keep NewIdSqlServerOrder both ways
            ["`WolverineOptions.EnvelopeIdGeneration`"] = (
                o => o.EnvelopeIdGeneration.ShouldBe(EnvelopeIdGeneration.GuidV7),
                o => o.EnvelopeIdGeneration.ShouldBe(EnvelopeIdGeneration.NewId)),
            // GH-5200
            ["`DurabilitySettings.CompressAgentCommands`"] = (
                o => o.Durability.CompressAgentCommands.ShouldBeTrue(),
                o => o.Durability.CompressAgentCommands.ShouldBeFalse()),
            // GH-5523. Wolverine hosts only, Production profile only; resolved the way UseWolverine() resolves it
            ["`JasperFxOptions.Production.GeneratedCodeMode` / `AssertAllPreGeneratedTypesExist` (Wolverine hosts)"] = (
                o => assertProductionProfile(o, TypeLoadMode.Static, true),
                o => assertProductionProfile(o, TypeLoadMode.Dynamic, false)),

            // e.g.
            // ["`WolverineOptions.Something`"] = (
            //     o => o.Something.ShouldBe(newValue),
            //     o => o.Something.ShouldBe(oldValue)),
        };

    [Fact]
    public void RestoreV6Defaults_marks_the_options_so_extension_packages_can_restore_their_defaults()
    {
        var options = new WolverineOptions();
        options.V6DefaultsRestored.ShouldBeFalse();

        options.RestoreV6Defaults();

        options.V6DefaultsRestored.ShouldBeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void an_explicit_AlwaysProvisionOptionalSchemaObjects_wins_over_RestoreV6Defaults(bool restoreFirst)
    {
        // GH-5193: whichever order the two are called in
        var options = new WolverineOptions();
        if (restoreFirst) options.RestoreV6Defaults();
        options.Durability.AlwaysProvisionOptionalSchemaObjects = true;
        if (!restoreFirst) options.RestoreV6Defaults();

        options.Durability.AlwaysProvisionOptionalSchemaObjects.ShouldBeTrue();
    }

    [Fact]
    public void RestoreV6Defaults_restores_every_core_default_flipped_in_7_0()
    {
        foreach (var (setting, (assertV7Default, assertV6Value)) in CoreRestorations)
        {
            var options = new WolverineOptions();
            shouldHold(setting, "the 7.0 default on fresh options", () => assertV7Default(options));

            options.RestoreV6Defaults();
            shouldHold(setting, "the 6.x value after RestoreV6Defaults()", () => assertV6Value(options));
        }
    }

    [Fact]
    public void every_row_of_the_7_0_defaults_table_names_a_restore_test()
    {
        var rows = readDefaultsTable();

        foreach (var row in rows)
        {
            row.RestoreTest.ShouldNotBeNullOrWhiteSpace(
                $"The 7.0 defaults row for {row.Setting} must name the test class that proves RestoreV6Defaults() restores it");

            if (row.RestoreTest == ThisTestClass)
            {
                CoreRestorations.ContainsKey(row.Setting).ShouldBeTrue(
                    $"The 7.0 defaults row for {row.Setting} names {ThisTestClass}, so it needs an entry in CoreRestorations");
            }
            else
            {
                testClassExists(row.RestoreTest).ShouldBeTrue(
                    $"The 7.0 defaults row for {row.Setting} names restore test class '{row.RestoreTest}', which was not found under src/");
            }
        }

        var listedInGuide = rows.Where(x => x.RestoreTest == ThisTestClass).Select(x => x.Setting).ToArray();
        foreach (var setting in CoreRestorations.Keys)
        {
            listedInGuide.ShouldContain(setting,
                $"CoreRestorations covers {setting}, but the migration guide's 7.0 defaults table has no row for it naming {ThisTestClass}");
        }
    }

    [Fact]
    public void RestoreV6Defaults_leaves_the_6_0_service_location_policy_alone()
    {
        // NotAllowed became the default in 6.0, so it is the 6.x value. Its 5.x opt-out,
        // RestoreV5Defaults(), was removed in 7.0.
        var options = new WolverineOptions();
        options.ServiceLocationPolicy.ShouldBe(ServiceLocationPolicy.NotAllowed);

        options.RestoreV6Defaults();

        options.ServiceLocationPolicy.ShouldBe(ServiceLocationPolicy.NotAllowed);
    }

    [Fact]
    public void RestoreV6Defaults_does_not_change_the_default_serializer()
    {
        var options = new WolverineOptions();
        var before = options.DefaultSerializer;

        options.RestoreV6Defaults();

        options.DefaultSerializer.ShouldBeSameAs(before);
    }

    private static void assertProductionProfile(WolverineOptions options, TypeLoadMode mode, bool assertion)
    {
        // This process is a test host, where the default is otherwise deliberately skipped
        options.ApplyProductionCodeGenerationUnderTestRunner = true;

        var jasperfx = new JasperFxOptions();
        WolverineOptions.MarkProductionCodeGenerationUnset(jasperfx);
        options.ResolveProductionCodeGeneration(jasperfx, assertionBefore: false);

        jasperfx.Production.GeneratedCodeMode.ShouldBe(mode);
        jasperfx.Production.AssertAllPreGeneratedTypesExist.ShouldBe(assertion);
    }

    private static void shouldHold(string setting, string what, Action assertion)
    {
        try
        {
            assertion();
        }
        catch (Exception e)
        {
            throw new ShouldAssertException($"{setting}: expected {what}. {e.Message}", e);
        }
    }

    private record DefaultsRow(string Setting, string RestoreTest);

    private const string TableHeading = "### Defaults that changed in 7.0";

    private static IReadOnlyList<DefaultsRow> readDefaultsTable()
    {
        var path = Path.Combine(repositoryRoot(), "docs", "guide", "migration.md");
        File.Exists(path).ShouldBeTrue($"Could not find the migration guide at {path}");

        var lines = File.ReadAllLines(path);
        var heading = Array.FindIndex(lines, x => x.StartsWith(TableHeading, StringComparison.Ordinal));
        heading.ShouldBeGreaterThanOrEqualTo(0, $"The migration guide has no '{TableHeading}' section");

        // The section runs until the next heading of the same or a higher level
        var end = Array.FindIndex(lines, heading + 1, x => x.StartsWith("## ", StringComparison.Ordinal) || x.StartsWith("### ", StringComparison.Ordinal));
        if (end < 0) end = lines.Length;

        var header = Array.FindIndex(lines, heading, end - heading,
            x => x.StartsWith("| Setting |", StringComparison.Ordinal));
        header.ShouldBeGreaterThan(heading, $"The '{TableHeading}' section has no '| Setting | ... |' table");

        var columns = splitRow(lines[header]);
        var settingColumn = Array.IndexOf(columns, "Setting");
        var restoreTestColumn = Array.IndexOf(columns, "Restore test");
        restoreTestColumn.ShouldBeGreaterThanOrEqualTo(0, "The 7.0 defaults table needs a 'Restore test' column");

        lines[header + 1].Trim().ShouldStartWith("|---", customMessage: "The 7.0 defaults table header must be followed by its separator row");

        var rows = new List<DefaultsRow>();
        for (var i = header + 2; i < end && lines[i].TrimStart().StartsWith('|'); i++)
        {
            var cells = splitRow(lines[i]);
            cells.Length.ShouldBe(columns.Length, $"Malformed 7.0 defaults table row: {lines[i]}");
            rows.Add(new DefaultsRow(cells[settingColumn], cells[restoreTestColumn].Trim('`')));
        }

        return rows;
    }

    private static string[] splitRow(string line)
    {
        return line.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
    }

    private static bool testClassExists(string className)
    {
        var src = Path.Combine(repositoryRoot(), "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Any(x => File.ReadAllText(x).Contains($"class {className}", StringComparison.Ordinal));
    }

    private static string repositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "wolverine.slnx")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("Could not find the repository root (the directory holding wolverine.slnx)");
        return directory.FullName;
    }
}
