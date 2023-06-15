using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace WolverineWebApi.Samples;

#region sample_using_as_mediator

public class MediatorController : ControllerBase
{
    [HttpPost("/question")]
    public Task<Answer> Get(Question question, [FromServices] IMessageBus bus)
    {
        // All the real processing happens in Wolverine
        return bus.InvokeAsync<Answer>(question);
    }
}

#endregion

public record Question;

public record Answer;