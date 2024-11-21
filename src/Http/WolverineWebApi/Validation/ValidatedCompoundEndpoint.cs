using FluentValidation;
using JasperFx.Core;
using Microsoft.AspNetCore.Http.HttpResults;
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

#region sample_using_optional_iresult_with_openapi_metadata

public class ValidatedCompoundEndpoint2
{
    public static User? Load(BlockUser2 cmd)
    {
        return cmd.UserId.IsNotEmpty() ? new User(cmd.UserId) : null;
    }

    // This method would be called, and if the NotFound value is
    // not null, will stop the rest of the processing
    // Likewise, Wolverine will use the NotFound type to add
    // OpenAPI metadata
    public static NotFound? Validate(User? user)
    {
        if (user == null)
            return (NotFound?)Results.NotFound<User>(user);

        return null;
    }

    [WolverineDelete("/optional/result")]
    public static  string Handle(BlockUser2 cmd, User user)
    {
        return "Ok - user blocked";
    }
}

#endregion

public record BlockUser(string? UserId);
public record BlockUser2(string? UserId);

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