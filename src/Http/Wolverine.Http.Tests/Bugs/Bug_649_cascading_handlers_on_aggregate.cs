using Alba;
using IntegrationTests;
using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Builder;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_649_cascading_handlers_on_aggregate
{
    /// <summary>
    /// Note that when this bug was discovered, it sometimes succeeded and sometimes failed
    /// </summary>
    [Fact]
    public async Task compiles_every_run()
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());

        builder.Services.AddMarten(opts =>
            {
                opts.Connection(Servers.PostgresConnectionString);
                opts.Events.StreamIdentity = StreamIdentity.AsString;
                opts.Projections.LiveStreamAggregation<Consultation>();
            })
            .IntegrateWithWolverine();
        
        builder.Host.UseWolverine();

        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });
        
        await host.ExecuteAndWaitAsync(async () =>
        {
            await host.Scenario(x =>
            {
                x.Post.Json(new StartConsultation(Guid.NewGuid().ToString(), new()
                {
                    "user1",
                    "user2",
                    "user3",
                }, Guid.NewGuid())).ToUrl("/api/v1/consultatie/start");
                x.StatusCodeShouldBe(204);
            });
        });
    }
}

public record ConsultationStarted(string ConsultationId, List<string> Members, Guid PatientId);
public record Consultation(string Id, List<string> Members, Guid PatientId)
{
    public static Consultation Create(ConsultationStarted @event) => new(@event.ConsultationId, @event.Members, @event.PatientId);
}

public record StartConsultation(string ConsultationId, List<string> Members, Guid PatientId);
public sealed class StartEndpoint
{
    [WolverinePost("api/v1/consultatie/start")]
    public static (IStartStream, OutgoingMessages) HandleAsync(StartConsultation command)
    {
        var startStream = MartenOps.StartStream<Consultation>(command.ConsultationId,
            new ConsultationStarted(command.ConsultationId, command.Members, command.PatientId));
        var messages = new OutgoingMessages
        {
            new GrantMembersAccessToPatientOfConsultation(command.ConsultationId, command.Members)
        };

        return (startStream, messages);
    }
}

public sealed record GrantMembersAccessToPatientOfConsultation(string ConsultationId, List<string> Members);
public static class GrantMembersAccessToPatientOfConsultationHandler
{
    public static OutgoingMessages HandleAsync(GrantMembersAccessToPatientOfConsultation command)
    {
        var messages = new OutgoingMessages();
        messages.AddRange(command.Members.Select(memberId => 
            new GrantMemberAccessToPatientOfConsultation(command.ConsultationId, memberId)));
        return messages;
    }
}

public sealed record GrantMemberAccessToPatientOfConsultation(string ConsultationId, string MemberId);

public static class GrantMemberAccessToPatientOfConsultationHandler
{
    [AggregateHandler]
    public static OutgoingMessages HandleAsync(GrantMemberAccessToPatientOfConsultation command, Consultation consultation)
    {
        return new();
    }
}