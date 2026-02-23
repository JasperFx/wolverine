using Wolverine.Shims.MediatR;

namespace Wolverine.Shims.Tests.MediatR;

public record RequestWithResponse(string Data) : IRequest<Response>;
public record Response(string Data, string ProcessedBy);

public record RequestWithoutResponse(string Data) : IRequest;


public record RequestCascade(string Data) : IRequest<CascadingMessage>;
public record CascadingMessage(string Data);

public record RequestAdditionFromService(int number) : IRequest<int>;


public interface IAdditionService
{
    int Process(int number);
}

public class AdditionService : IAdditionService
{
    public int Process(int number)
    {
        return number + 1;
    }

}
