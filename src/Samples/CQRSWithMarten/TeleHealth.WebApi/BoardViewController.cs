using Marten;
using Marten.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using TeleHealth.Common;

namespace TeleHealth.WebApi;

public class BoardViewController : ControllerBase
{
    [HttpGet("/board/{id}")]
    public Task GetBoardView(Guid id, [FromServices] IQuerySession session)
    {
        return session.Json.WriteById<BoardView>(id, HttpContext);
    }

    [HttpGet("/boards")]
    public Task AllBoards([FromServices] IQuerySession session)
    {
        return session.Query<BoardView>().WriteArray(HttpContext);
    }
}
