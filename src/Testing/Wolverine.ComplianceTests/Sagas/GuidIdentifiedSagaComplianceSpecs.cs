using System.Diagnostics;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Persistence.Sagas;
using Xunit;

namespace Wolverine.ComplianceTests.Sagas;

[WolverineIgnore]
public class GuidBasicWorkflow : BasicWorkflow<GuidStart, GuidCompleteThree, Guid>
{
    public void Starts(WildcardStart start)
    {
        var sagaId = Guid.Parse(start.Id);
        Id = sagaId;
        Name = start.Name;
    }

    public void Handles(GuidDoThree message)
    {
        ThreeCompleted = true;
    }

    // GH-5345: user code trying to set LastModified, which the persistence frame must overwrite
    public void Handles(GuidBackdateLastModified message)
    {
        LastModified = message.Value;
    }
}

public class GuidDoThree
{
    [SagaIdentity] public Guid TheSagaId { get; set; }
}

public class GuidBackdateLastModified
{
    [SagaIdentity] public Guid TheSagaId { get; set; }
    public DateTimeOffset Value { get; set; }
}

public class GuidIdentifiedSagaComplianceSpecs<T> : SagaTestHarness<GuidBasicWorkflow> where T : ISagaHost, new()
{
    private readonly Guid stateId = Guid.NewGuid();

    public GuidIdentifiedSagaComplianceSpecs() : base(new T())
    {
    }

    [Fact]
    public async Task complete()
    {
        await send(new GuidStart
        {
            Id = stateId,
            Name = "Croaker"
        });

        await send(new FinishItAll(), stateId);

        (await LoadState(stateId)).ShouldBeNull();
    }

    [Fact]
    public async Task handle_a_saga_message_with_cascading_messages_passes_along_the_saga_id_in_header()
    {
        await send(new GuidStart
        {
            Id = stateId,
            Name = "Croaker"
        });

        Debug.WriteLine(await codeFor<GuidStart>());
        Debug.WriteLine(await codeFor<CompleteOne>());

        await send(new CompleteOne(), stateId);

        var state = await LoadState(stateId);
        state!.OneCompleted.ShouldBeTrue();
        state.TwoCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task start_1()
    {
        await send(new GuidStart
        {
            Id = stateId,
            Name = "Croaker"
        });

        var state = await LoadState(stateId);

        state.ShouldNotBeNull();
        state.Name.ShouldBe("Croaker");
    }

    [Fact]
    public async Task start_2()
    {
        await send(new WildcardStart
        {
            Id = stateId.ToString(),
            Name = "One Eye"
        });

        var state = await LoadState(stateId);

        state.ShouldNotBeNull();
        state.Name.ShouldBe("One Eye");
    }

    [Fact]
    public async Task straight_up_update_with_the_saga_id_on_the_message()
    {
        await send(new GuidStart
        {
            Id = stateId,
            Name = "Croaker"
        });

        var message = new GuidCompleteThree
        {
            SagaId = stateId
        };

        await send(message);

        var state = await LoadState(stateId);
        state!.ThreeCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task update_expecting_the_saga_id_to_be_on_the_envelope()
    {
        await send(new GuidStart
        {
            Id = stateId,
            Name = "Croaker"
        });

        await send(new CompleteFour(), stateId);

        var state = await LoadState(stateId);
        state!.FourCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task update_with_message_that_uses_saga_identity_attributed_property()
    {
        await send(new GuidStart
        {
            Id = stateId,
            Name = "Croaker"
        });

        var message = new GuidDoThree
        {
            TheSagaId = stateId
        };

        await send(message);

        var state = await LoadState(stateId);
        state!.ThreeCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task update_with_no_saga_id_to_be_on_the_envelope()
    {
        await Should.ThrowAsync<IndeterminateSagaStateIdException>(async () => { await invoke(new CompleteFour()); });
    }

    // GH-5345. A store may load LastModified from its own clock (Marten maps mt_last_modified), and the
    // database container's clock is not the test process's, so "now" is checked with a tolerance; ordering is
    // checked strictly between two values read back from the same store.
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task inserting_a_saga_sets_last_modified()
    {
        var before = DateTimeOffset.UtcNow;

        await send(new GuidStart
        {
            Id = stateId,
            Name = "Croaker"
        });

        var state = await LoadState(stateId);
        state.ShouldNotBeNull();
        state.LastModified.ShouldNotBe(default);
        state.LastModified.ShouldBeGreaterThan(before - ClockTolerance);
        state.LastModified.ShouldBeLessThan(DateTimeOffset.UtcNow + ClockTolerance);
    }

    [Fact]
    public async Task updating_a_saga_moves_last_modified_forward()
    {
        await send(new GuidStart
        {
            Id = stateId,
            Name = "Croaker"
        });

        var inserted = (await LoadState(stateId))!.LastModified;

        // Some stores keep timestamps to the millisecond or the second (SQLite's datetime())
        await Task.Delay(1100);

        await send(new GuidDoThree { TheSagaId = stateId });

        var state = await LoadState(stateId);
        state!.ThreeCompleted.ShouldBeTrue();
        state.LastModified.ShouldBeGreaterThan(inserted);
    }

    [Fact]
    public async Task a_last_modified_set_by_user_code_is_overwritten_on_save()
    {
        await send(new GuidStart
        {
            Id = stateId,
            Name = "Croaker"
        });

        var backdated = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await send(new GuidBackdateLastModified { TheSagaId = stateId, Value = backdated });

        var state = await LoadState(stateId);
        state!.LastModified.ShouldNotBe(backdated);
        state.LastModified.ShouldBeGreaterThan(DateTimeOffset.UtcNow - ClockTolerance);
    }

    [Fact]
    public async Task update_with_no_saga_id_to_be_on_the_envelope_or_message()
    {
        await Should.ThrowAsync<IndeterminateSagaStateIdException>(async () =>
        {
            await invoke(new GuidCompleteThree());
        });
    }
}