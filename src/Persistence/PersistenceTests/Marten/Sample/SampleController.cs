using System.Threading.Tasks;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Wolverine;
using Wolverine.Marten;

namespace PersistenceTests.Marten.Sample;

public class SampleController : ControllerBase
{
    #region sample_using_outbox_with_marten_in_mvc_action

    public async Task<IActionResult> PostCreateUser(
        [FromBody] CreateUser user,
        
        // This service is a specialized IMessagePublisher
        [FromServices] IMartenOutbox outbox,
        [FromServices] IDocumentSession session)
    {
        session.Store(new User { Name = user.Name });

        var @event = new UserCreated { UserName = user.Name };

        await outbox.PublishAsync(@event);

        await session.SaveChangesAsync();

        return Ok();
    }

    #endregion
}