using Bobcat;
using Bobcat.Engine;

namespace Wolverine.Bobcat.Tests;

// GH-4836: verify any result object — not only an HTTP response — as a table or structurally.
[Collection(nameof(AppointmentsCollection))]
public class verifying_objects(AppointmentsHost app) : WolverineSpec(app.Host)
{
    private static readonly Patient ann = new("Ann", new Address("Austin"));

    [Fact]
    public async Task selected_properties_including_nested_paths_as_a_table()
    {
        var recording = await Recordings.RecordAsync(() =>
        {
            Verify(ann, """
                        | Name | Address.City |
                        | Ann  | Boston       |
                        """);
            return Task.CompletedTask;
        });

        var step = recording.Steps.Single();
        step.Text.ShouldBe("the Patient has");
        step.Cells.Single(x => x.Name == "Name").Status.ShouldBe(ResultStatus.success);
        step.Cells.Single(x => x.Name == "Address.City").Status.ShouldBe(ResultStatus.failed);
    }

    [Fact]
    public void outside_a_recording_a_table_mismatch_throws()
    {
        Should.Throw<SpecificationFailedException>(() => Verify(ann, """
                                                                      | Address.City |
                                                                      | Boston       |
                                                                      """));
    }

    [Fact]
    public void structurally_with_ignored_members()
    {
        ThenMatches(ann, Expect.Value(new Patient("Ann", new Address("Elsewhere"))).Ignoring(x => x.Address));
        Should.Throw<SpecificationFailedException>(() => ThenMatches(ann, new Patient("Bob", ann.Address)));
    }
}
