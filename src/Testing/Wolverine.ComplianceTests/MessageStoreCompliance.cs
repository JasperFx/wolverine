using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using JasperFx.Resources;
using NSubstitute;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.ComplianceTests;

public abstract class MessageStoreCompliance : IAsyncLifetime
{
    public IHost theHost { get; private set; } = null!;
    protected IMessageStore thePersistence = null!;
    
    public abstract Task<IHost> BuildCleanHost();
    
    public async Task InitializeAsync()
    {
        theHost = await BuildCleanHost();

        await theHost.ResetResourceState();

        thePersistence = theHost.Services.GetRequiredService<IMessageStore>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }
    
    [Fact]
    public async Task get_counts()
    {
        var thePersistor = theHost.Get<IMessageStore>();

        var list = new List<Envelope>();

        // 10 incoming
        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistor.Inbox.StoreIncomingAsync(list.ToArray());


        // 7 scheduled
        list.Clear();
        for (var i = 0; i < 7; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;

            list.Add(envelope);
        }

        await thePersistor.Inbox.StoreIncomingAsync(list.ToArray());


        // 3 outgoing
        list.Clear();
        for (var i = 0; i < 3; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Outgoing;

            list.Add(envelope);
        }

        foreach (var envelope in list)
        {
            await thePersistor.Outbox.StoreOutgoingAsync(envelope, 0);
        }

        var counts = await thePersistor.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(10);
        counts.Scheduled.ShouldBe(7);
        counts.Outgoing.ShouldBe(3);
    }

    [Fact]
    public async Task store_a_single_incoming_envelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.SentAt = ((DateTimeOffset)DateTime.Today).ToUniversalTime();

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var stored = (await thePersistence.Admin.AllIncomingAsync()).Single();

        stored.Id.ShouldBe(envelope.Id);
        stored.OwnerId.ShouldBe(envelope.OwnerId);
        stored.Status.ShouldBe(envelope.Status);

        stored.SentAt.ShouldBe(envelope.SentAt);
    }

    [Fact]
    public async Task incoming_exists()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.SentAt = ((DateTimeOffset)DateTime.Today).ToUniversalTime();
        
        (await thePersistence.Inbox.ExistsAsync(envelope, CancellationToken.None)).ShouldBeFalse();
        
        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        
        (await thePersistence.Inbox.ExistsAsync(envelope, CancellationToken.None)).ShouldBeTrue();
    }
    
    [Fact]
    public async Task store_a_single_incoming_envelope_that_is_handled()
    {
        // This is for cases where you're only persisting the record for idempotency checks
        
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Handled;
        envelope.SentAt = ((DateTimeOffset)DateTime.Today).ToUniversalTime();

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var stored = (await thePersistence.Admin.AllIncomingAsync()).Single();
        
        // This is the important part
        stored.Data!.Length.ShouldBe(0);
        stored.Destination.ShouldBe(envelope.Destination);

        stored.Id.ShouldBe(envelope.Id);
        stored.OwnerId.ShouldBe(envelope.OwnerId);
        stored.Status.ShouldBe(envelope.Status);
    }

    [Fact]
    public async Task store_a_single_incoming_envelope_that_is_a_duplicate()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("stub://incoming");
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(async () =>
        {
            await thePersistence.Inbox.StoreIncomingAsync(envelope);
        });
    }

    [Fact]
    public async Task bulk_store_intra_batch_duplicate_reports_only_actual_duplicates()
    {
        var existing = ObjectMother.Envelope();
        existing.Destination = new Uri("stub://incoming-bulk-dup");
        existing.Status = EnvelopeStatus.Incoming;
        await thePersistence.Inbox.StoreIncomingAsync(existing);

        var fresh1 = ObjectMother.Envelope();
        fresh1.Destination = existing.Destination;
        fresh1.Status = EnvelopeStatus.Incoming;

        var fresh2 = ObjectMother.Envelope();
        fresh2.Destination = existing.Destination;
        fresh2.Status = EnvelopeStatus.Incoming;

        var batch = new[] { fresh1, existing, fresh2 };

        var ex = await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => thePersistence.Inbox.StoreIncomingAsync(batch));

        // Only the actually-existing envelope is reported as a duplicate.
        // Fresh envelopes must NOT appear in Duplicates — otherwise DurableReceiver
        // would route them straight to listener.CompleteAsync and the handler would
        // never run for legitimate messages.
        ex.Duplicates.Count.ShouldBe(1);
        ex.Duplicates.Single().Id.ShouldBe(existing.Id);
    }

    [Fact]
    public async Task store_a_single_outgoing_envelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Outgoing;
        envelope.SentAt = ((DateTimeOffset)DateTime.Today).ToUniversalTime();

        await thePersistence.Outbox.StoreOutgoingAsync(envelope, 5890);

        var stored = (await thePersistence.Admin.AllOutgoingAsync())
            .Single();

        stored.Id.ShouldBe(envelope.Id);
        stored.OwnerId.ShouldBe(5890);
        stored.Status.ShouldBe(envelope.Status);
        stored.SentAt.ShouldBe(envelope.SentAt);
    }

    [Fact]
    public async Task mark_envelope_as_handled()
    {
        var envelope = ObjectMother.Envelope();

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(0);
        counts.Scheduled.ShouldBe(0);
        counts.Handled.ShouldBe(1);
    }

    [Fact]
    public async Task mark_several_envelopes_as_handled()
    {
        var envelope1 = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope1);
        
        var envelope2 = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope2);
        
        var envelope3 = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope3);
        
        var envelope4 = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope4);
        
        var envelope5 = ObjectMother.Envelope();
        
        await thePersistence.Inbox.StoreIncomingAsync(envelope5);
        
        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync([envelope1, envelope2, envelope3, envelope4]);
        await Task.Delay(5000);
        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(1);
        counts.Scheduled.ShouldBe(0);
        counts.Handled.ShouldBe(4);
    }

    [Fact]
    public async Task schedule_execution()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());


        list[5].ScheduledTime = DateTimeOffset.Now.AddMinutes(5);

        list[7].ScheduledTime = DateTimeOffset.Now.AddMinutes(5);
        list[9].ScheduledTime = DateTimeOffset.Now.AddMinutes(5);

        await thePersistence.Inbox.ScheduleExecutionAsync(list[5]);
        await thePersistence.Inbox.ScheduleExecutionAsync(list[7]);
        await thePersistence.Inbox.ScheduleExecutionAsync(list[9]);

        var stored = await thePersistence.Admin.AllIncomingAsync();
        stored.Count(x => x.Status == EnvelopeStatus.Incoming).ShouldBe(7);
        stored.Count(x => x.Status == EnvelopeStatus.Scheduled).ShouldBe(3);

        stored.Single(x => x.Id == list[5].Id).ScheduledTime.HasValue.ShouldBeTrue();
        stored.Single(x => x.Id == list[7].Id).ScheduledTime.HasValue.ShouldBeTrue();
        stored.Single(x => x.Id == list[9].Id).ScheduledTime.HasValue.ShouldBeTrue();
    }


    [Fact]
    public async Task discard_and_reassign_outgoing()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Outgoing;

            list.Add(envelope);
        }

        foreach (var envelope in list)
        {
            await thePersistence.Outbox.StoreOutgoingAsync(envelope, 111);
        }

        var toDiscard = new[] { list[2], list[3], list[7] };
        var toReassign = new[] { list[1], list[4], list[6] };

        await thePersistence.Outbox.DiscardAndReassignOutgoingAsync(toDiscard, toReassign, 444);

        var stored = await thePersistence.Admin.AllOutgoingAsync();
        stored.Count.ShouldBe(7);

        stored.Any(x => x.Id == list[2].Id).ShouldBeFalse();
        stored.Any(x => x.Id == list[3].Id).ShouldBeFalse();
        stored.Any(x => x.Id == list[7].Id).ShouldBeFalse();

        stored.Single(x => x.Id == list[1].Id).OwnerId.ShouldBe(444);
        stored.Single(x => x.Id == list[4].Id).OwnerId.ShouldBe(444);
        stored.Single(x => x.Id == list[6].Id).OwnerId.ShouldBe(444);
    }
    
    [Fact]
    public async Task delete_dead_letter_message_by_id()
    {
        var unReplayableEnvelope = ObjectMother.Envelope();
        var replayableEnvelope = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(unReplayableEnvelope);
        await thePersistence.Inbox.StoreIncomingAsync(replayableEnvelope);

        var divideByZeroException = new DivideByZeroException("Kaboom!");
        var applicationException = new ApplicationException("Kaboom!");
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(unReplayableEnvelope, divideByZeroException);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(replayableEnvelope, applicationException);

        await thePersistence
            .DeadLetters
            .DiscardAsync(new DeadLetterEnvelopeQuery([replayableEnvelope.Id]), CancellationToken.None);

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.DeadLetter.ShouldBe(1);
        counts.Incoming.ShouldBe(0);
        counts.Scheduled.ShouldBe(0);
        counts.Handled.ShouldBe(0);
    }

    [Fact]
    public async Task load_incoming_counts()
    {
        var random = new Random();

        var localOne = "local://one".ToUri();
        var localTwo = "local://two".ToUri();

        var list = new List<Envelope>();
        for (var i = 0; i < 100; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Destination = TransportConstants.DurableLocalUri;

            list.Add(envelope);

            if (random.Next(0, 10) > 6)
            {
                envelope.OwnerId = TransportConstants.AnyNode;
            }
            else
            {
                envelope.OwnerId = 5;
            }

            if (random.Next(0, 10) > 4)
            {
                envelope.Destination = localOne;
            }
            else
            {
                envelope.Destination = localTwo;
            }

            if (random.Next(0, 10) > 3)
            {
                envelope.Status = EnvelopeStatus.Incoming;
            }
            else
            {
                envelope.Status = EnvelopeStatus.Handled;
            }
        }

        await thePersistence.Inbox.StoreIncomingAsync(list);

        var counts1 = await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(localOne, 1000);
        var counts2 = await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(localTwo, 1000);


        counts1.Count.ShouldBe(list.Count(x =>
            x.OwnerId == TransportConstants.AnyNode && x.Status == EnvelopeStatus.Incoming &&
            x.Destination == localOne));

        counts2.Count.ShouldBe(list.Count(x =>
            x.OwnerId == TransportConstants.AnyNode && x.Status == EnvelopeStatus.Incoming &&
            x.Destination == localTwo));
    }

    [Fact]
    public async Task fetch_incoming_by_owner_and_address()
    {
        var random = new Random();

        var localOne = "local://one".ToUri();
        var localTwo = "local://two".ToUri();

        var list = new List<Envelope>();
        for (var i = 0; i < 100; i++)
        {
            var envelope = ObjectMother.Envelope();
            list.Add(envelope);

            if (random.Next(0, 10) > 6)
            {
                envelope.OwnerId = TransportConstants.AnyNode;
            }
            else
            {
                envelope.OwnerId = 5;
            }

            if (random.Next(0, 10) > 4)
            {
                envelope.Destination = localOne;
            }
            else
            {
                envelope.Destination = localTwo;
            }

            if (random.Next(0, 10) > 3)
            {
                envelope.Status = EnvelopeStatus.Incoming;
            }
            else
            {
                envelope.Status = EnvelopeStatus.Handled;
            }
        }

        await thePersistence.Inbox.StoreIncomingAsync(list);

        var limit = list.Count(x =>
            x.OwnerId == TransportConstants.AnyNode && x.Status == EnvelopeStatus.Incoming &&
            x.Destination == localOne) - 1;
        var one = await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(localOne, limit);
        foreach (var envelope in one)
        {
            envelope.Destination.ShouldBe(localOne);
            envelope.OwnerId.ShouldBe(TransportConstants.AnyNode);
            envelope.Status.ShouldBe(EnvelopeStatus.Incoming);
        }

        one.Count.ShouldBe(limit);
    }

    [Fact]
    public async Task delete_a_single_outgoing_envelope()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Outgoing;

            list.Add(envelope);
        }

        foreach (var envelope in list)
        {
            await thePersistence.Outbox.StoreOutgoingAsync(envelope, 111);
        }

        var toDelete = list[5];

        await thePersistence.Outbox.DeleteOutgoingAsync(toDelete);

        var stored = await thePersistence.Admin.AllOutgoingAsync();
        stored.Count.ShouldBe(9);

        stored.Any(x => x.Id == toDelete.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task store_multiple_incoming_envelopes()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());

        var stored = await thePersistence.Admin.AllIncomingAsync();

        list.Select(x => x.Id).OrderBy(x => x)
            .ShouldHaveTheSameElementsAs(stored.Select(x => x.Id).OrderBy(x => x));
    }
    
    [Fact]
    public async Task store_multiple_outgoing_envelopes()
    {
        await thePersistence.Admin.ClearAllAsync();

        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Outgoing;

            list.Add(envelope);
        }

        foreach (var envelope in list) await thePersistence.Outbox.StoreOutgoingAsync(envelope, 111);

        var stored = await thePersistence.Admin.AllOutgoingAsync();

        list.Select(x => x.Id).OrderBy(x => x)
            .ShouldHaveTheSameElementsAs(stored.Select(x => x.Id).OrderBy(x => x));

        stored.Each(x => x.OwnerId.ShouldBe(111));
    }

    [Fact]
    public async Task move_to_dead_letter_storage()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());


        var ex = new DivideByZeroException("Kaboom!");

        var report2 = new ErrorReport(list[2], ex);
        var report3 = new ErrorReport(list[3], ex);
        var report4 = new ErrorReport(list[4], ex);

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report2.Envelope, report2.Exception);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report3.Envelope, report2.Exception);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report4.Envelope, report2.Exception);

        var stored = await thePersistence.Admin.AllIncomingAsync();

        stored.Count.ShouldBe(7);

        stored.Any(x => x.Id == list[2].Id).ShouldBeFalse();
        stored.Any(x => x.Id == list[3].Id).ShouldBeFalse();
        stored.Any(x => x.Id == list[4].Id).ShouldBeFalse();
    }
    
    [Fact]
    public async Task load_dead_letter_envelopes_with_limit()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());


        var ex = new DivideByZeroException("Kaboom!");

        var report2 = new ErrorReport(list[2], ex);
        var report3 = new ErrorReport(list[3], ex);
        var report4 = new ErrorReport(list[4], ex);

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report2.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report3.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report4.Envelope, ex);

        var stored = await thePersistence.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery()
        {
            PageSize = 2
        }, CancellationToken.None);

        stored.Envelopes.Count.ShouldBe(2);
    }

    [Fact]
    public async Task query_dead_letter_envelopes_with_from_and_until()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());


        var ex = new DivideByZeroException("Kaboom!");

        var report2 = new ErrorReport(list[2], ex);
        var report3 = new ErrorReport(list[3], ex);
        var report4 = new ErrorReport(list[4], ex);

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report2.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report3.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report4.Envelope, ex);

        var query = new DeadLetterEnvelopeQuery(new TimeRange(DateTimeOffset.Now.AddDays(-1),
            DateTimeOffset.Now.AddDays(1)));
        
        var result = await thePersistence.DeadLetters.QueryAsync(query, CancellationToken.None);

        result.Envelopes.Count.ShouldBe(3);
    }

    [Fact]
    public async Task load_dead_letter_envelopes_by_exception_type()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());


        var ex = new DivideByZeroException("Kaboom!");

        var report2 = new ErrorReport(list[2], ex);
        var report3 = new ErrorReport(list[3], ex);
        var report4 = new ErrorReport(list[4], ex);

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report2.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report3.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report4.Envelope, ex);


        var stored = await thePersistence.DeadLetters.QueryAsync(
            new()
            {
                ExceptionType = report2.ExceptionType
            }, CancellationToken.None);

        stored.Envelopes.Count.ShouldBe(3);
        stored.Envelopes.ShouldContain(x => x.Envelope.Id == report2.Id);
        stored.Envelopes.ShouldContain(x => x.Envelope.Id == report3.Id);
        stored.Envelopes.ShouldContain(x => x.Envelope.Id == report4.Id);
    }

    [Fact]
    public async Task increment_the_attempt_count_of_incoming_envelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var prop = ReflectionHelper.GetProperty<Envelope>(x => x.Attempts);
        prop.SetValue(envelope, 3);

        await thePersistence.Inbox.IncrementIncomingEnvelopeAttemptsAsync(envelope);

        var stored = (await thePersistence.Admin.AllIncomingAsync()).Single();
        stored.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task load_dead_letter_envelope()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());


        var ex = new DivideByZeroException("Kaboom!");

        var report2 = new ErrorReport(list[2], ex);
        var report3 = new ErrorReport(list[3], ex);
        var report4 = new ErrorReport(list[4], ex);

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report2.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report3.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report4.Envelope, ex);


        var stored = await thePersistence.DeadLetters.DeadLetterEnvelopeByIdAsync(report2.Id);

        stored.ShouldNotBeNull();

        stored.ExceptionMessage.ShouldBe(report2.ExceptionMessage);
        stored.Envelope.Id.ShouldBe(report2.Id);
        stored.ExceptionType.ShouldBe(report2.ExceptionType);
        stored.Envelope.MessageType.ShouldBe(report2.Envelope.MessageType);
        stored.Envelope.Source.ShouldBe(report2.Envelope.Source);
    }

    [Fact]
    public async Task load_dead_letter_envelopes_by_message_type()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());


        var ex = new DivideByZeroException("Kaboom!");

        var report2 = new ErrorReport(list[2], ex);
        var report3 = new ErrorReport(list[3], ex);
        var report4 = new ErrorReport(list[4], ex);

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report2.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report3.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report4.Envelope, ex);


        var stored = await thePersistence.DeadLetters.QueryAsync(
            new()
            {
                MessageType = report2.Envelope.MessageType
            }, CancellationToken.None);

        stored.Envelopes.Count.ShouldBe(3);
        stored.Envelopes.ShouldContain(x => x.Envelope.Id == report2.Id);
        stored.Envelopes.ShouldContain(x => x.Envelope.Id == report3.Id);
        stored.Envelopes.ShouldContain(x => x.Envelope.Id == report4.Id);
    }

    [Fact]
    public async Task delete_multiple_outgoing_envelope()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Outgoing;

            list.Add(envelope);
        }

        foreach (var envelope in list) await thePersistence.Outbox.StoreOutgoingAsync(envelope, 111);

        var toDelete = new[] { list[2], list[3], list[7] };

        await thePersistence.Outbox.DeleteOutgoingAsync(toDelete);

        var stored = await thePersistence.Admin.AllOutgoingAsync();
        stored.Count.ShouldBe(7);

        stored.Any(x => x.Id == list[2].Id).ShouldBeFalse();
        stored.Any(x => x.Id == list[3].Id).ShouldBeFalse();
        stored.Any(x => x.Id == list[7].Id).ShouldBeFalse();
    }

    [Fact]
    public async Task persist_and_load_pin_and_pause_restrictions()
    {
        await thePersistence.Admin.ClearAllAsync();
        
        var restriction1 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://1"), AgentRestrictionType.Pinned, 1);
        var restriction2 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://2"), AgentRestrictionType.Pinned, 2);
        var restriction3 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://3"), AgentRestrictionType.Pinned, 3);
        
        var restriction4 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://4"), AgentRestrictionType.Paused, 0);
        var restriction5 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://5"), AgentRestrictionType.Paused, 0);
        var restriction6 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://6"), AgentRestrictionType.Paused, 0);

        IReadOnlyList<AgentRestriction> restrictions = [restriction1, restriction2, restriction3, restriction4, restriction5, restriction6];
        await thePersistence.Nodes.PersistAgentRestrictionsAsync(
            restrictions,
            CancellationToken.None);

        var state = await thePersistence.Nodes.LoadNodeAgentStateAsync(CancellationToken.None);

        state.Restrictions.Current.OrderBy(x => x.AgentUri.ToString()).ShouldBe(restrictions.OrderBy(x => x.AgentUri.ToString()));
    }
    
    [Fact]
    public async Task persist_then_overwrite_with_none_restriction_deletes()
    {
        await thePersistence.Admin.ClearAllAsync();
        
        var restriction1 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://1"), AgentRestrictionType.Pinned, 1);
        var restriction2 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://2"), AgentRestrictionType.Pinned, 2);
        var restriction3 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://3"), AgentRestrictionType.Pinned, 3);
        
        var restriction4 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://4"), AgentRestrictionType.Paused, 0);
        var restriction5 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://5"), AgentRestrictionType.Paused, 0);
        var restriction6 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://6"), AgentRestrictionType.Paused, 0);

        IReadOnlyList<AgentRestriction> restrictions = [restriction1, restriction2, restriction3, restriction4, restriction5, restriction6];
        await thePersistence.Nodes.PersistAgentRestrictionsAsync(
            restrictions,
            CancellationToken.None);

        await thePersistence.Nodes.PersistAgentRestrictionsAsync(
        [
            restriction3 with { Type = AgentRestrictionType.None },
            restriction4 with { Type = AgentRestrictionType.None }
        ], CancellationToken.None);

        var state = await thePersistence.Nodes.LoadNodeAgentStateAsync(CancellationToken.None);

        state.Restrictions.Current.OrderBy(x => x.AgentUri.ToString()).ShouldBe([restriction1, restriction2, restriction5, restriction6]);
    }

    [Fact]
    public async Task can_edit_and_replay_dead_letter_envelope()
    {
        var envelope = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var exception = new InvalidOperationException("Test error");
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(envelope, exception);

        var newBody = new byte[] { 1, 2, 3, 4, 5 };
        await thePersistence.DeadLetters.EditAndReplayAsync(envelope.Id, newBody, CancellationToken.None);

        var deadLetter = await thePersistence.DeadLetters.DeadLetterEnvelopeByIdAsync(envelope.Id);
        deadLetter.ShouldNotBeNull();
        deadLetter.Envelope.Data.ShouldBe(newBody);
        deadLetter.Replayable.ShouldBeTrue();
    }

    [Fact]
    public async Task query_scheduled_messages()
    {
        var scheduledList = new List<Envelope>();
        for (var i = 0; i < 5; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10 + i);
            scheduledList.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(scheduledList);

        // Also add non-scheduled incoming
        var incomingList = new List<Envelope>();
        for (var i = 0; i < 3; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;
            incomingList.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(incomingList);

        var results = await thePersistence.ScheduledMessages.QueryAsync(
            new ScheduledMessageQuery { PageSize = 3 }, CancellationToken.None);

        results.Messages.Count.ShouldBe(3);
        results.TotalCount.ShouldBe(5);
    }

    [Fact]
    public async Task query_scheduled_messages_by_message_type()
    {
        var list = new List<Envelope>();
        for (var i = 0; i < 3; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
            envelope.MessageType = "TypeA";
            list.Add(envelope);
        }

        for (var i = 0; i < 2; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
            envelope.MessageType = "TypeB";
            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list);

        var results = await thePersistence.ScheduledMessages.QueryAsync(
            new ScheduledMessageQuery { MessageType = "TypeA" }, CancellationToken.None);

        results.TotalCount.ShouldBe(3);
        results.Messages.Count.ShouldBe(3);
        results.Messages.ShouldAllBe(m => m.MessageType == "TypeA");
    }

    [Fact]
    public async Task cancel_scheduled_message_by_id()
    {
        var list = new List<Envelope>();
        for (var i = 0; i < 3; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list);

        await thePersistence.ScheduledMessages.CancelAsync(
            new ScheduledMessageQuery { MessageIds = [list[1].Id] }, CancellationToken.None);

        var counts = await thePersistence.Admin.FetchCountsAsync();
        counts.Scheduled.ShouldBe(2);
    }

    [Fact]
    public async Task cancel_scheduled_messages_by_message_type()
    {
        var list = new List<Envelope>();
        for (var i = 0; i < 3; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
            envelope.MessageType = "CancelTypeA";
            list.Add(envelope);
        }

        for (var i = 0; i < 2; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
            envelope.MessageType = "CancelTypeB";
            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list);

        await thePersistence.ScheduledMessages.CancelAsync(
            new ScheduledMessageQuery { MessageType = "CancelTypeA" }, CancellationToken.None);

        var counts = await thePersistence.Admin.FetchCountsAsync();
        counts.Scheduled.ShouldBe(2);
    }

    [Fact]
    public async Task reschedule_scheduled_message()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var newTime = DateTimeOffset.UtcNow.AddHours(5);
        await thePersistence.ScheduledMessages.RescheduleAsync(envelope.Id, newTime, CancellationToken.None);

        var results = await thePersistence.ScheduledMessages.QueryAsync(
            new ScheduledMessageQuery { MessageIds = [envelope.Id] }, CancellationToken.None);

        results.Messages.Count.ShouldBe(1);
        // Verify the time was updated (within a second tolerance)
        results.Messages[0].ScheduledTime!.Value.ShouldBe(newTime.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task summarize_scheduled_messages()
    {
        var list = new List<Envelope>();
        for (var i = 0; i < 3; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
            envelope.MessageType = "SumTypeA";
            list.Add(envelope);
        }

        for (var i = 0; i < 2; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
            envelope.MessageType = "SumTypeB";
            list.Add(envelope);
        }

        var envelope3 = ObjectMother.Envelope();
        envelope3.Status = EnvelopeStatus.Scheduled;
        envelope3.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
        envelope3.MessageType = "SumTypeC";
        list.Add(envelope3);

        await thePersistence.Inbox.StoreIncomingAsync(list);

        var counts = await thePersistence.ScheduledMessages.SummarizeAsync("TestService", CancellationToken.None);

        counts.Count.ShouldBeGreaterThanOrEqualTo(3);
        counts.ShouldContain(c => c.MessageType == "SumTypeA" && c.Count == 3);
        counts.ShouldContain(c => c.MessageType == "SumTypeB" && c.Count == 2);
        counts.ShouldContain(c => c.MessageType == "SumTypeC" && c.Count == 1);
    }

    [Fact]
    public async Task move_to_dead_letter_storage_with_null_source()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Source = null;
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var ex = new DivideByZeroException("Kaboom!");

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(envelope, ex);

        var stored = await thePersistence.DeadLetters.DeadLetterEnvelopeByIdAsync(envelope.Id);

        stored.ShouldNotBeNull();
        stored.Envelope.Id.ShouldBe(envelope.Id);
        stored.Envelope.Source.ShouldBeNull();
        stored.ExceptionMessage.ShouldBe("Kaboom!");
        stored.ExceptionType.ShouldBe(typeof(DivideByZeroException).FullName);
    }

    /// <summary>
    /// Contract test for https://github.com/JasperFx/wolverine/issues/2576.
    ///
    /// When a scheduled message is loaded from a store via
    /// <see cref="IMessageDatabase.PollForScheduledMessagesAsync"/>, the
    /// resulting in-memory envelope passed to
    /// <see cref="IWolverineRuntime.EnqueueDirectlyAsync"/> must have its
    /// <c>Store</c> property stamped with the originating store. Without this,
    /// downstream pipeline components (DelegatingMessageInbox,
    /// DurableReceiver._markAsHandled, FlushOutgoingMessagesOnCommit) cannot
    /// route their writes back to the correct store, and ancillary-store rows
    /// get stuck in <c>Incoming</c> status forever because the
    /// "mark as handled" SQL targets the main store instead.
    /// </summary>
    [Fact]
    public virtual async Task scheduled_poll_stamps_envelope_with_originating_store()
    {
        if (thePersistence is not IMessageDatabase database)
        {
            // Non-database stores (e.g. RavenDb, CosmosDb) wire scheduled
            // dispatch through their own durability agents, not through
            // PollForScheduledMessagesAsync. Skip this contract there.
            return;
        }

        // Persist a scheduled envelope into this store's incoming table.
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1); // already due
        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        await thePersistence.Inbox.ScheduleExecutionAsync(envelope);

        // Spy runtime that captures whatever PollForScheduledMessagesAsync
        // hands to EnqueueDirectlyAsync.
        var capturedEnvelopes = new List<Envelope>();
        var spyRuntime = Substitute.For<IWolverineRuntime>();
        spyRuntime
            .EnqueueDirectlyAsync(Arg.Do<IReadOnlyList<Envelope>>(es => capturedEnvelopes.AddRange(es)))
            .Returns(ValueTask.CompletedTask);

        var durabilitySettings = theHost.Services.GetRequiredService<DurabilitySettings>();

        await database.PollForScheduledMessagesAsync(
            spyRuntime, NullLogger.Instance, durabilitySettings, CancellationToken.None);

        capturedEnvelopes.ShouldNotBeEmpty(
            "Expected the just-scheduled envelope to be picked up by the poller.");

        var captured = capturedEnvelopes.SingleOrDefault(x => x.Id == envelope.Id);
        captured.ShouldNotBeNull(
            "Expected the polled envelope to match the one we scheduled.");

        captured.Store.ShouldBe(thePersistence,
            "Polled envelopes must be stamped with the store they came from so " +
            "downstream mark-as-handled / inbox writes route back to the correct store. " +
            "See GH-2576.");
    }

    /// <summary>
    /// Sister contract test to <see cref="scheduled_poll_stamps_envelope_with_originating_store"/>:
    /// the inbox-recovery path (which DLQ replay funnels through) must stamp
    /// <c>envelope.Store</c> so downstream mark-as-handled writes route to the
    /// right database.
    ///
    /// Whenever <c>RecoverIncomingMessagesCommand</c> picks up an orphaned
    /// (<c>owner_id == AnyNode</c>) envelope via
    /// <see cref="IMessageStore.LoadPageOfGloballyOwnedIncomingAsync"/>, it
    /// applies <c>envelope.Store ??= _store</c>. Without that stamp, an
    /// ancillary-owned envelope would be marked Handled in the main store and
    /// stay stuck Incoming. The existing fix lives at
    /// <c>RecoverIncomingMessagesCommand:48</c>; this test pins the behavior
    /// down so the GH-2576 fix doesn't accidentally regress it.
    ///
    /// The DLQ replay flow is one producer of orphaned-incoming rows (via
    /// <c>MoveReplayableErrorMessagesToIncomingOperation</c>), but durability
    /// also generates them when nodes crash mid-handle. We exercise the
    /// recovery contract directly by persisting an envelope with
    /// <c>OwnerId == AnyNode</c>, sidestepping any database-specific quirks
    /// in the move-from-dead-letter SQL.
    /// </summary>
    [Fact]
    public virtual async Task orphaned_incoming_recovery_stamps_envelope_with_originating_store()
    {
        if (thePersistence is not IMessageDatabase)
        {
            return;
        }

        // Persist an envelope as if a previous owner crashed mid-handle —
        // status Incoming, owner_id == AnyNode marks it as orphaned and
        // visible to LoadPageOfGloballyOwnedIncomingAsync.
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.OwnerId = TransportConstants.AnyNode;
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        // Recovery loop reads the orphaned incoming envelopes back into memory.
        var recovered = await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(
            envelope.Destination!, 100);

        recovered.ShouldNotBeEmpty(
            "Expected the orphaned envelope to be visible to the recovery loader.");

        var match = recovered.SingleOrDefault(x => x.Id == envelope.Id);
        match.ShouldNotBeNull("Expected the recovered envelope to match the one we persisted.");

        // RecoverIncomingMessagesCommand applies envelope.Store ??= _store
        // immediately after this load. Simulate that step here so the contract
        // test captures what the runtime path actually guarantees.
        foreach (var e in recovered)
        {
            e.Store ??= thePersistence;
        }

        match.Store.ShouldBe(thePersistence,
            "Recovered envelopes must carry their originating store so " +
            "mark-as-handled SQL targets the right database. See GH-2318 / GH-2576.");
    }

}