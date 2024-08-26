using System.Diagnostics;
using JasperFx.Core;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Embedded;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb.Internals;
using Xunit.Abstractions;

namespace RavenDbTests;

public class playing
{
    private readonly ITestOutputHelper _output;

    public playing(ITestOutputHelper output)
    {
        _output = output;
    }

    //[Fact]
    public async Task try_to_persist_envelope()
    {
        EmbeddedServer.Instance.StartServer();
        using var store = await EmbeddedServer.Instance.GetDocumentStoreAsync("Testing");

        var lockObject = new DistributedLockObject
        {
            ExpiresAt = DateTimeOffset.UtcNow + 5.Minutes()
        };

        string lockId = "lock1";

        PrintWork(store);
        
        // var envelope = ObjectMother.Envelope();
        // var incoming = new IncomingMessage(envelope);
        //
        // using var session1 = store.OpenAsyncSession();
        // await session1.StoreAsync(incoming);
        // await session1.SaveChangesAsync();
        //
        // using var session2 = store.OpenAsyncSession();
        // var incoming2 = await session2.LoadAsync<IncomingMessage>(incoming.Id, CancellationToken.None);
        //
        // incoming2.Status.ShouldBe(incoming.Status);
        // incoming2.OwnerId.ShouldBe(incoming.OwnerId);
        // incoming2.MessageType.ShouldBe(incoming.MessageType);
        // incoming2.Body.ShouldBe(incoming.Body);
    }
    
public void PrintWork(IDocumentStore store) 
{
    // Try to get hold of the printer resource
    long reservationIndex = LockResource(store, "Printer/First-Floor", TimeSpan.FromMinutes(20));

    try
    {
        _output.WriteLine("GOT THE LOCK!");
        // Do some work for the duration that was set.
        // Don't exceed the duration, otherwise resource is available for someone else.
    }
    finally
    {
        ReleaseResource(store, "Printer/First-Floor", reservationIndex);
    }
}

public long LockResource(IDocumentStore store, string resourceName, TimeSpan duration)
{
    while (true)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        SharedResource resource = new SharedResource
        {
            ReservedUntil = now.Add(duration)
        };

        CompareExchangeResult<SharedResource> saveResult = store.Operations.Send(
                new PutCompareExchangeValueOperation<SharedResource>(resourceName, resource, 0));

        if (saveResult.Successful)
        {
            // resourceName wasn't present - we managed to reserve
            return saveResult.Index;
        }

        // At this point, Put operation failed - someone else owns the lock or lock time expired
        if (saveResult.Value.ReservedUntil < now)
        {
            // Time expired - Update the existing key with the new value
            CompareExchangeResult<SharedResource> takeLockWithTimeoutResult = store.Operations.Send(
                new PutCompareExchangeValueOperation<SharedResource>(resourceName, resource, saveResult.Index));

            if (takeLockWithTimeoutResult.Successful)
            {
                return takeLockWithTimeoutResult.Index;
            }
        }

        // Wait a little bit and retry
        Thread.Sleep(20);
    }
}

public void ReleaseResource(IDocumentStore store, string resourceName, long index)
{
    CompareExchangeResult<SharedResource> deleteResult
        = store.Operations.Send(new DeleteCompareExchangeValueOperation<SharedResource>(resourceName, index));

    // We have 2 options here:
    // deleteResult.Successful is true - we managed to release resource
    // deleteResult.Successful is false - someone else took the lock due to timeout 
}
}

public class DistributedLockObject
{
    public DateTimeOffset? ExpiresAt { get; set; }
}

public class SharedResource
{
    public DateTimeOffset? ReservedUntil { get; set; }
}

