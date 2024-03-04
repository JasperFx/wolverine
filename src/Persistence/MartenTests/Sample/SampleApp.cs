using IntegrationTests;
using Marten;
using Marten.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Sample;

public class MessageInvocationTests : PostgresqlContext, IDisposable
{
    private readonly IHost theHost;

    public MessageInvocationTests()
    {
        theHost = WolverineHost.For(opts =>
        {
            opts.PublishAllMessages().Locally();

            opts.Services.AddSingleton<UserNames>();

            opts.Services.AddMarten(Servers.PostgresConnectionString)
                .IntegrateWithWolverine();
        });

        theHost.Get<IDocumentStore>().Advanced.Clean.CompletelyRemoveAll();
    }

    public void Dispose()
    {
        theHost?.Dispose();
    }


    [Fact]
    public async Task using_ExecuteAndWaitSync()
    {
        await theHost.ExecuteAndWaitAsync(x => x.InvokeAsync(new CreateUser { Name = "Tom" }));


        await using (var session = theHost.Get<IDocumentStore>().QuerySession())
        {
            session.Load<User>("Tom").ShouldNotBeNull();
        }

        theHost.Get<UserNames>()
            .Names.Single().ShouldBe("Tom");
    }


    [Fact]
    public async Task using_InvokeMessageAndWait()
    {
        await theHost.ExecuteAndWaitAsync(x => x.InvokeAsync(new CreateUser { Name = "Bill" }));

        await using (var session = theHost.Get<IDocumentStore>().QuerySession())
        {
            session.Load<User>("Bill").ShouldNotBeNull();
        }

        theHost.Get<UserNames>()
            .Names.Single().ShouldBe("Bill");
    }
}

public class UserHandler
{
    #region sample_UserHandler_handle_CreateUser

    [Transactional]
    public static UserCreated Handle(CreateUser message, IDocumentSession session)
    {
        session.Store(new User { Name = message.Name });

        return new UserCreated { UserName = message.Name };
    }

    #endregion

    public static void Handle(UserCreated message, UserNames names)
    {
        names.Names.Add(message.UserName);
    }
}

public class CreateUser
{
    public string Name;
}

public class UserCreated
{
    public string UserName;
}

public class UserNames
{
    public readonly IList<string> Names = new List<string>();
}

public class User
{
    [Identity] public string Name;
}