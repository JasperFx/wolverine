using FluentValidation;
using Wolverine.Attributes;
using Wolverine.Http;

namespace WolverineWebApi.Validation;

public class ValidatedCompoundEndpoint
{
    public static User? Load(BlockUser cmd)
    {
        if (cmd.UserId == null)
            throw new ApplicationException("Something went wrong. Fluent validation should stop execution before load");

        return new User(cmd.UserId);
    }
    
    public static IResult Validate(User user)
    {
        if (user == null)
            throw new ApplicationException("Something went wrong. Fluent validation should stop execution before load");

        return WolverineContinue.Result();
    }
    
    [WolverineDelete("/validate/user-compound")]
    public static  string Handle(BlockUser cmd, User user)
    {
        return "Ok - user blocked";
    }
  
}

public record BlockUser(string? UserId);

public class BlockUserValidator : AbstractValidator<BlockUser>
{
    public BlockUserValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
    }
}

public class User
{
    public string Id { get; private set; }
    public bool IsBlocked { get; private set; }
    
    public User(string id)
    {
        Id = id;
        IsBlocked = false;
    }

    public void Block()
    {
        IsBlocked = true;
    }
}