using Wolverine.Http;

namespace OpenApiDemonstrator;

public static class Endpoints
{
    [WolverineGet("/json")]
    public static ResponseModel GetReservation()
    {
        return new ResponseModel();
    }

    [WolverinePost("/message"), EmptyResponse]
    public static Message1 PostMessage()
    {
        return new Message1();
    }
}

public class Message1;

public class ResponseModel
{
    public string Name { get; set; } = null!;
    public int Age { get; set; }
}