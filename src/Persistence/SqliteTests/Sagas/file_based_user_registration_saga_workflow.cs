using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence.Sagas;
using Wolverine.Sqlite;
using Wolverine.Tracking;

namespace SqliteTests.Sagas;

public class file_based_user_registration_saga_workflow
{
    [Fact]
    public async Task starts_registration_and_sends_verification_email()
    {
        using var database = Servers.CreateDatabase("user_registration_saga");
        var audit = new UserRegistrationWorkflowAudit();
        IHost? host = null;

        try
        {
            host = await startHost(database.ConnectionString, audit);

            var userId = Guid.NewGuid().ToString("N");
            const string email = "alicia@example.com";
            await host.InvokeMessageAndWaitAsync(new RegisterUser(userId, email));

            File.Exists(database.DatabaseFile).ShouldBeTrue();

            audit.VerificationEmails.Count.ShouldBe(1);
            var verify = audit.VerificationEmails.Single();
            verify.UserId.ShouldBe(userId);
            verify.Email.ShouldBe(email);
            audit.Activations.Count.ShouldBe(0);
            audit.Rejections.Count.ShouldBe(0);
        }
        finally
        {
            await stopHost(host);
        }
    }

    [Fact]
    public async Task happy_path_verifies_and_activates_user()
    {
        using var database = Servers.CreateDatabase("user_registration_saga");
        var audit = new UserRegistrationWorkflowAudit();
        IHost? host = null;

        try
        {
            host = await startHost(database.ConnectionString, audit);

            var userId = Guid.NewGuid().ToString("N");
            const string email = "bruno@example.com";
            await host.InvokeMessageAndWaitAsync(new RegisterUser(userId, email));
            await host.InvokeMessageAndWaitAsync(new EmailVerified(userId));

            audit.Activations.Count.ShouldBe(1);
            var activation = audit.Activations.Single();
            activation.UserId.ShouldBe(userId);
            activation.Email.ShouldBe(email);
            audit.Rejections.Count.ShouldBe(0);

            await host.InvokeMessageAndWaitAsync(new UserActivated(userId));
            await Should.ThrowAsync<UnknownSagaException>(async () =>
                await host.InvokeMessageAndWaitAsync(new EmailVerified(userId)));
        }
        finally
        {
            await stopHost(host);
        }
    }

    [Fact]
    public async Task expiration_path_rejects_registration_and_completes()
    {
        using var database = Servers.CreateDatabase("user_registration_saga");
        var audit = new UserRegistrationWorkflowAudit();
        IHost? host = null;

        try
        {
            host = await startHost(database.ConnectionString, audit);

            var userId = Guid.NewGuid().ToString("N");
            const string reason = "verification expired";
            await host.InvokeMessageAndWaitAsync(new RegisterUser(userId, "cassie@example.com"));
            await host.InvokeMessageAndWaitAsync(new VerificationExpired(userId, reason));

            audit.Rejections.Count.ShouldBe(1);
            var rejection = audit.Rejections.Single();
            rejection.UserId.ShouldBe(userId);
            rejection.Reason.ShouldBe(reason);
            audit.Activations.Count.ShouldBe(0);

            await host.InvokeMessageAndWaitAsync(new RegistrationRejected(userId, reason));
            await Should.ThrowAsync<UnknownSagaException>(async () =>
                await host.InvokeMessageAndWaitAsync(new VerificationExpired(userId, reason)));
        }
        finally
        {
            await stopHost(host);
        }
    }

    [Fact]
    public async Task can_resume_registration_saga_after_host_restart_with_file_database()
    {
        using var database = Servers.CreateDatabase("user_registration_saga");
        var audit = new UserRegistrationWorkflowAudit();
        IHost? firstHost = null;
        IHost? secondHost = null;

        try
        {
            firstHost = await startHost(database.ConnectionString, audit);

            var userId = Guid.NewGuid().ToString("N");
            const string email = "devan@example.com";
            await firstHost.InvokeMessageAndWaitAsync(new RegisterUser(userId, email));
            audit.VerificationEmails.Count.ShouldBe(1);

            await stopHost(firstHost);
            firstHost = null;

            secondHost = await startHost(database.ConnectionString, audit);

            await secondHost.InvokeMessageAndWaitAsync(new EmailVerified(userId));
            await secondHost.InvokeMessageAndWaitAsync(new UserActivated(userId));

            audit.VerificationEmails.Count.ShouldBe(1);
            audit.Activations.Count.ShouldBe(1);
            audit.Rejections.Count.ShouldBe(0);

            await Should.ThrowAsync<UnknownSagaException>(async () =>
                await secondHost.InvokeMessageAndWaitAsync(new EmailVerified(userId)));
        }
        finally
        {
            await stopHost(firstHost);
            await stopHost(secondHost);
        }
    }

    [Fact]
    public async Task unknown_saga_id_for_domain_event_throws_unknown_saga_exception()
    {
        using var database = Servers.CreateDatabase("user_registration_saga");
        var audit = new UserRegistrationWorkflowAudit();
        IHost? host = null;

        try
        {
            host = await startHost(database.ConnectionString, audit);

            await Should.ThrowAsync<UnknownSagaException>(async () =>
                await host.InvokeMessageAndWaitAsync(new EmailVerified(Guid.NewGuid().ToString("N"))));
        }
        finally
        {
            await stopHost(host);
        }
    }

    private static async Task<IHost> startHost(string connectionString, UserRegistrationWorkflowAudit audit)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(UserRegistrationSaga))
                    .IncludeType(typeof(UserRegistrationExternalHandlers));

                opts.UseSqlitePersistenceAndTransport(connectionString)
                    .AutoProvision();

                opts.Services.AddSingleton(audit);
            }).StartAsync();
    }

    private static async Task stopHost(IHost? host)
    {
        if (host == null) return;

        await host.StopAsync();
        host.Dispose();
    }

}

public class UserRegistrationWorkflowAudit
{
    public ConcurrentQueue<SendVerificationEmail> VerificationEmails { get; } = new();
    public ConcurrentQueue<ActivateUser> Activations { get; } = new();
    public ConcurrentQueue<RejectRegistration> Rejections { get; } = new();
}

public class UserRegistrationExternalHandlers
{
    public static void Handle(SendVerificationEmail command, UserRegistrationWorkflowAudit audit)
    {
        audit.VerificationEmails.Enqueue(command);
    }

    public static void Handle(ActivateUser command, UserRegistrationWorkflowAudit audit)
    {
        audit.Activations.Enqueue(command);
    }

    public static void Handle(RejectRegistration command, UserRegistrationWorkflowAudit audit)
    {
        audit.Rejections.Enqueue(command);
    }
}

public enum UserRegistrationStatus
{
    PendingVerification,
    EmailVerified,
    VerificationExpired,
    Activated,
    Rejected
}

public record RegisterUser(string UserId, string Email);

public record SendVerificationEmail(string UserId, string Email);

public record EmailVerified([property: SagaIdentity] string UserId);

public record VerificationExpired([property: SagaIdentity] string UserId, string Reason);

public record ActivateUser(string UserId, string Email);

public record RejectRegistration(string UserId, string Reason);

public record UserActivated([property: SagaIdentity] string UserId);

public record RegistrationRejected([property: SagaIdentity] string UserId, string Reason);

public class UserRegistrationSaga : Saga
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public UserRegistrationStatus Status { get; set; } = UserRegistrationStatus.PendingVerification;

    public static (UserRegistrationSaga, SendVerificationEmail) Start(RegisterUser command)
    {
        return (
            new UserRegistrationSaga
            {
                Id = command.UserId,
                Email = command.Email,
                Status = UserRegistrationStatus.PendingVerification
            },
            new SendVerificationEmail(command.UserId, command.Email)
        );
    }

    public ActivateUser Handle(EmailVerified message)
    {
        Status = UserRegistrationStatus.EmailVerified;
        return new ActivateUser(message.UserId, Email);
    }

    public RejectRegistration Handle(VerificationExpired message)
    {
        Status = UserRegistrationStatus.VerificationExpired;
        return new RejectRegistration(message.UserId, message.Reason);
    }

    public void Handle(UserActivated message)
    {
        Status = UserRegistrationStatus.Activated;
        MarkCompleted();
    }

    public void Handle(RegistrationRejected message)
    {
        Status = UserRegistrationStatus.Rejected;
        MarkCompleted();
    }
}
