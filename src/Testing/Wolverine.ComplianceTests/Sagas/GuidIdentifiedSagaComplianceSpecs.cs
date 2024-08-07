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
}

public class GuidDoThree
{
    [SagaIdentity] public Guid TheSagaId { get; set; }
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

        Debug.WriteLine(codeFor<GuidStart>());
        Debug.WriteLine(codeFor<CompleteOne>());

        await send(new CompleteOne(), stateId);

        var state = await LoadState(stateId);
        state.OneCompleted.ShouldBeTrue();
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
        state.ThreeCompleted.ShouldBeTrue();
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
        state.FourCompleted.ShouldBeTrue();
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
        state.ThreeCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task update_with_no_saga_id_to_be_on_the_envelope()
    {
        await Should.ThrowAsync<IndeterminateSagaStateIdException>(async () => { await invoke(new CompleteFour()); });
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