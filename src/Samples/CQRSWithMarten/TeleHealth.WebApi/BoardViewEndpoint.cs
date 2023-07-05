using Marten;
using Marten.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using TeleHealth.Common;
using Wolverine.Http;

namespace TeleHealth.WebApi;

public class BoardViewEndpoint 
{
    [WolverineGet("/board/{id}")]
    public Task GetBoardView(Guid id, IQuerySession session, HttpContext context) 
        => session.Json.WriteById<BoardView>(id, context);

    [HttpGet("/boards")]
    public Task AllBoards(IQuerySession session, HttpContext context) 
        => session.Query<BoardView>().WriteArray(context);
}