using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime;

public class SendingEndpoint<T>
{
    public async ValueTask<string> SendAsync(T message, IMessageContext bus, HttpResponse response)
    {
        await bus.SendAsync(message);
        response.StatusCode = 202;
        return "Success.";
    }
}