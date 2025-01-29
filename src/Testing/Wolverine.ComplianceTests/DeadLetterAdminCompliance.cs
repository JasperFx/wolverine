using System.Diagnostics;
using Castle.Components.DictionaryAdapter;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute.Core;
using Oakton.Resources;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.ComplianceTests;

public abstract class DeadLetterAdminCompliance : IAsyncLifetime
{
    protected const string ServiceName = "Service1";
    private readonly ITestOutputHelper _output;

    protected DeadLetterAdminCompliance(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly string[] _colors =
        ["Red", "Blue", "Orange", "Yellow", "Purple", "Green", "Black", "White", "Gray", "Pink"];
    
    public IHost theHost { get; private set; }
    protected IMessageStore thePersistence;
    protected IDeadLetterAdminService theDeadLetters;
    protected EnvelopeGenerator theGenerator;

    private DateTimeOffset FiveHoursAgo;
    private DateTimeOffset FourHoursAgo;
    private DateTimeOffset SixHoursAgo;
    private DateTimeOffset SevenHoursAgo;
    private DateTimeOffset EightHoursAgo;
    private IReadOnlyList<DeadLetterQueueCount> theSummaries;

    public abstract Task<IHost> BuildCleanHost();
    
    public async Task InitializeAsync()
    {
        theHost = await BuildCleanHost();

        await theHost.ResetResourceState();

        thePersistence = theHost.Services.GetRequiredService<IMessageStore>();
        theDeadLetters = (IDeadLetterAdminService)thePersistence.DeadLetters;

        theGenerator = new EnvelopeGenerator();
        theGenerator.MessageSource = BuildRandomMessage;
        
        FourHoursAgo = DateTimeOffset.UtcNow.AddHours(-4);
        FiveHoursAgo = DateTimeOffset.UtcNow.AddHours(-5);
        SixHoursAgo = DateTimeOffset.UtcNow.AddHours(-6);
        SevenHoursAgo = DateTimeOffset.UtcNow.AddHours(-7);
        EightHoursAgo = DateTimeOffset.UtcNow.AddHours(-8);
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    protected Task load(int count, DateTimeOffset startingTime)
    {
        theGenerator.StartingTime = startingTime;
        return theGenerator.WriteDeadLetters(count, thePersistence);
    }

    protected async Task theStoredDeadLettersAre(params EnvelopeGenerator[] generators)
    {
        await thePersistence.Admin.RebuildAsync();

        foreach (var generator in generators)
        {
            await generator.WriteDeadLetters(thePersistence);
        }
    }

    public static object BuildRandomMessage()
    {
        var color = _colors[Random.Shared.Next(0, 9)];
        var number = Random.Shared.Next(0, 5); // purposely doing more of message 4
        switch (number)
        {
            case 0:
                return new TargetMessage1(Guid.NewGuid(), Random.Shared.Next(1, 10), color);
            case 1:
                return new TargetMessage2(Guid.NewGuid(), Random.Shared.Next(1, 10), color);
            default:
                return new TargetMessage3(Guid.NewGuid(), Random.Shared.Next(1, 10), color);
        }
    }

    private void withTargetMessage1()
    {
        theGenerator.MessageSource = () =>
        {
            var color = _colors[Random.Shared.Next(0, 9)];
            var number = Random.Shared.Next(0, 10);
            return new TargetMessage1(Guid.NewGuid(), number, color);
        };
    }
    
    private void withTargetMessage2()
    {
        theGenerator.MessageSource = () =>
        {
            var color = _colors[Random.Shared.Next(0, 9)];
            var number = Random.Shared.Next(0, 10);
            return new TargetMessage2(Guid.NewGuid(), number, color);
        };
    }
    
    private void withTargetMessage3()
    {
        theGenerator.MessageSource = () =>
        {
            var color = _colors[Random.Shared.Next(0, 9)];
            var number = Random.Shared.Next(0, 10);
            return new TargetMessage3(Guid.NewGuid(), number, color);
        };
    }
    
    private async Task fetchSummary(TimeRange range)
    {
        theSummaries = await theDeadLetters.SummarizeAllAsync(ServiceName, range, CancellationToken.None);
    }

    private DeadLetterQueueCount summaryCount<TMessage, TException>(int expected, Uri? receivedAt = null, string? databaseIdentifier = null)
    {
        var uri = receivedAt ?? theGenerator.ReceivedAt;
        var messageType = typeof(TMessage).ToMessageTypeName();
        var exceptionType = typeof(TException).FullNameInCode();

        databaseIdentifier ??= "default";

        return new DeadLetterQueueCount(ServiceName, uri, messageType, exceptionType, databaseIdentifier, expected);
    }

    [Fact]
    public async Task get_all_summaries_with_no_options()
    {
        withTargetMessage1();
        theGenerator.ExceptionSource = msg => new InvalidOperationException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);
        await load(8, SevenHoursAgo);
        
        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(3, FiveHoursAgo);
        await load(2, SixHoursAgo);

        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(10, EightHoursAgo);
        await load(8, SevenHoursAgo);
        
        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);

        theGenerator.ReceivedAt = new Uri("local://one");
        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(11, EightHoursAgo);
        await load(3, SevenHoursAgo);

        await fetchSummary(TimeRange.AllTime());
        
        theSummaries.ShouldContain(summaryCount<TargetMessage1, InvalidOperationException>(20, TransportConstants.DurableLocalUri));
        theSummaries.ShouldContain(summaryCount<TargetMessage1, DivideByZeroException>(5, TransportConstants.DurableLocalUri));
        theSummaries.ShouldContain(summaryCount<TargetMessage2, DivideByZeroException>(12, TransportConstants.DurableLocalUri));
        theSummaries.ShouldContain(summaryCount<TargetMessage2, BadImageFormatException>(14, new Uri("local://one")));
    }



    // [Fact]
    // public async Task get_summaries_after_a_time()
    // {
    //     throw new NotImplementedException();
    // }
    //
    // [Fact]
    // public async Task get_summaries_before_a_time()
    // {
    //     throw new NotImplementedException();
    // }
    //
    // [Fact]
    // public async Task get_summaries_within_a_time_range()
    // {
    //     throw new NotImplementedException();
    // }
    //
    // [Fact]
    // public async Task envelopes_paging_and_totals()
    // {
    //     throw new NotImplementedException();
    // }
    //
    // [Fact]
    // public async Task envelopes_query_by_combination_of_factors()
    // {
    //     throw new NotImplementedException();
    // }
    //
    // [Fact]
    // public async Task discard_by_query()
    // {
    //     throw new NotImplementedException();
    // }
    //
    // [Fact]
    // public async Task replay_by_query()
    // {
    //     throw new NotImplementedException();
    // }
    //
    // [Fact]
    // public async Task replay_by_ids()
    // {
    //     throw new NotImplementedException();
    // }
    //
    //
    
    
}

public record TargetMessage1(Guid Id, int Number, string Color);
public record TargetMessage2(Guid Id, int Number, string Color);
public record TargetMessage3(Guid Id, int Number, string Color);

