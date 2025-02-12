using JasperFx.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Http;

public class DeadLetterEnvelopeGetRequest
{
    /// <summary>
    /// Number of records to return per page.
    /// </summary>
    public uint Limit { get; set; } = 100;
    /// <summary>
    /// Fetch records starting after the record with this ID.
    /// </summary>
    public Guid? StartId { get; set; }
    public string? MessageType { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? Until { get; set; }
    public string? TenantId { get; set; }
}

public class DeadLetterEnvelopeIdsRequest
{
    public Guid[] Ids { get; set; }
    public string? TenantId { get; set; }
}

public record DeadLetterEnvelopesFoundResponse(IReadOnlyList<DeadLetterEnvelopeResponse> Messages, Guid? NextId);

public record DeadLetterEnvelopeResponse(
    Guid Id,
    DateTimeOffset? ExecutionTime,
    object? Body,
    string MessageType,
    string ReceivedAt,
    string Source,
    string ExceptionType,
    string ExceptionMessage,
    DateTimeOffset SentAt,
    bool Replayable);

public static class DeadLettersEndpointExtensions
{
    /// <summary>
    /// Add endpoints to manage the Wolverine database-backed deal letter queue for this
    /// application.
    /// </summary>
    /// <param name="groupUrlPrefix">Optionally override the group Url prefix for these endpoints. The default is "/dead-letters"</param>
    public static RouteGroupBuilder MapDeadLettersEndpoints(this IEndpointRouteBuilder endpoints, string? groupUrlPrefix = "/dead-letters")
    {
        if (groupUrlPrefix.IsEmpty())
            throw new ArgumentNullException(nameof(groupUrlPrefix), "Cannot be empty or null");
        var deadlettersGroup = endpoints.MapGroup(groupUrlPrefix);

        deadlettersGroup.MapPost("/", async (DeadLetterEnvelopeGetRequest request, IMessageStore messageStore, HandlerGraph handlerGraph, IOptions<WolverineOptions> opts) =>
        {
            var deadLetters = messageStore.DeadLetters;
            var queryParameters = new DeadLetterEnvelopeQueryParameters
            {
                Limit = request.Limit,
                StartId = request.StartId,
                MessageType = request.MessageType,
                ExceptionType = request.ExceptionType,
                ExceptionMessage = request.ExceptionMessage,
                From = request.From,
                Until = request.Until
            };
            var deadLetterEnvelopesFound = await deadLetters.QueryDeadLetterEnvelopesAsync(queryParameters, request.TenantId);
            return new DeadLetterEnvelopesFoundResponse(
                [.. deadLetterEnvelopesFound.DeadLetterEnvelopes.Select(x => new DeadLetterEnvelopeResponse(
                    x.Id,
                    x.ExecutionTime,
                    handlerGraph.TryFindMessageType(x.MessageType, out var messageType) ? opts.Value.DetermineSerializer(x.Envelope).ReadFromData(messageType, x.Envelope) : null,
                    x.MessageType,
                    x.ReceivedAt,
                    x.Source,
                    x.ExceptionType,
                    x.ExceptionMessage,
                    x.SentAt,
                    x.Replayable))
                ],
                deadLetterEnvelopesFound.NextId);
        });

        deadlettersGroup.MapPost("/replay", (DeadLetterEnvelopeIdsRequest request, IMessageStore messageStore) =>
            messageStore.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync(request.Ids, request.TenantId));

        deadlettersGroup.MapDelete("/", ([FromBody]DeadLetterEnvelopeIdsRequest request, IMessageStore messageStore) =>
            messageStore.DeadLetters.DeleteDeadLetterEnvelopesAsync(request.Ids, request.TenantId));

        return deadlettersGroup;
    }
}