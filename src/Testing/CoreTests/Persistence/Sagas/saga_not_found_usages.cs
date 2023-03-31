using System;
using System.Linq;
using System.Threading.Tasks;
using TestingSupport.Sagas;
using Wolverine.Persistence.Sagas;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Persistence.Sagas;

public class saga_not_found_usages : SagaTestHarness<SteppedSaga>
{
    private readonly ITestOutputHelper _output;

    public saga_not_found_usages(ITestOutputHelper output) : base(new InMemorySagaHost())
    {
        _output = output;
    }

    [Fact]
    public async Task call_not_found_when_saga_does_not_exist()
    {
        withApplication();

        _output.WriteLine(codeFor<CompleteOne>());

        var completeOne = new CompleteOne();
        await send(completeOne, Guid.NewGuid());

        SteppedSaga.NotFoundCommand.ShouldBe(completeOne);
    }

    [Fact]
    public async Task not_found_not_called_if_the_saga_is_found()
    {
        withApplication();

        SteppedSaga.NotFoundCommand = null;

        var id = Guid.NewGuid();
        await send(new GuidStart { Id = id });

        var completeOne = new CompleteOne();
        await send(completeOne, id);

        SteppedSaga.NotFoundCommand.ShouldBeNull();
    }

    [Fact]
    public async Task should_throw_exception_on_saga_not_found_with_no_alternative_handler()
    {
        withApplication();

        var sagaId = Guid.NewGuid();

        var wrapper = await Should.ThrowAsync<AggregateException>(async () =>
        {
            await send(new GuidCompleteThree { SagaId = sagaId });
        });

        var ex = wrapper.InnerExceptions.OfType<UnknownSagaException>().FirstOrDefault();
        ex.ShouldNotBeNull();
        ex.Message.ShouldContain(
            $"Could not find an expected saga document of type CoreTests.Persistence.Sagas.SteppedSaga for id '{sagaId}'");
    }

    [Fact]
    public async Task StartOrHandle_works_if_new()
    {
        withApplication();

        var id = Guid.NewGuid();
        await send(new CompleteTwo(), id);

        var state = await LoadState(id);

        state.ShouldNotBeNull();
        state.TwoCompleted.ShouldBeTrue();
    }
}

public class SteppedSaga : Saga
{
    public Guid Id { get; set; }

    public bool OneCompleted { get; set; }
    public bool TwoCompleted { get; set; }
    public bool ThreeCompleted { get; set; }
    public bool FourCompleted { get; set; }

    public static CompleteOne NotFoundCommand { get; set; }

    public void Start(GuidStart start)
    {
        Id = start.Id;
    }

    public CompleteTwo Handle(CompleteOne one)
    {
        OneCompleted = true;
        return new CompleteTwo();
    }

    // TODO -- this has to be static
    public static void NotFound(CompleteOne one)
    {
        NotFoundCommand = one;
    }

    public CompleteFour StartOrHandle(CompleteTwo two, Envelope envelope)
    {
        Id = Guid.Parse(envelope.SagaId);
        TwoCompleted = true;
        return new CompleteFour();
    }

    public void Handle(GuidCompleteThree message)
    {
        ThreeCompleted = true;
    }
}