using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.ComplianceTests;

public abstract class DeadLetterAdminCompliance : IAsyncLifetime
{
    protected const string ServiceName = "Service1";

    private static readonly string[] _colors =
        ["Red", "Blue", "Orange", "Yellow", "Purple", "Green", "Black", "White", "Gray", "Pink"];

    private readonly ITestOutputHelper _output;
    private DeadLetterEnvelopeResults allEnvelopes;
    private DateTimeOffset EightHoursAgo;

    private DateTimeOffset FiveHoursAgo;
    private DateTimeOffset FourHoursAgo;
    private DateTimeOffset SevenHoursAgo;
    private DateTimeOffset SixHoursAgo;
    protected IDeadLetterAdminService theDeadLetters;
    protected EnvelopeGenerator theGenerator;
    protected IMessageStore thePersistence;
    private IReadOnlyList<DeadLetterQueueCount> theSummaries;

    protected DeadLetterAdminCompliance(ITestOutputHelper output)
    {
        _output = output;
    }

    public IHost theHost { get; private set; }

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

    public abstract Task<IHost> BuildCleanHost();

    protected Task load(int count, DateTimeOffset startingTime)
    {
        theGenerator.StartingTime = startingTime;
        return theGenerator.WriteDeadLetters(count, thePersistence);
    }

    protected async Task theStoredDeadLettersAre(params EnvelopeGenerator[] generators)
    {
        await thePersistence.Admin.RebuildAsync();

        foreach (var generator in generators) await generator.WriteDeadLetters(thePersistence);
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

        if (theSummaries.Any())
        {
            _output.WriteLine("Summaries were:");
            foreach (var summary in theSummaries) _output.WriteLine(summary.ToString());
        }
        else
        {
            _output.WriteLine("No summaries were found!");
        }
    }

    private DeadLetterQueueCount summaryCount<TMessage, TException>(int expected, Uri? receivedAt = null,
        Uri databaseIdentifier = null)
    {
        var uri = receivedAt ?? theGenerator.ReceivedAt;
        var messageType = typeof(TMessage).ToMessageTypeName();
        var exceptionType = typeof(TException).FullNameInCode();

        databaseIdentifier ??= theHost.GetRuntime().Storage.Uri;

        return new DeadLetterQueueCount(ServiceName, uri, messageType, exceptionType, databaseIdentifier, expected);
    }

    protected void noCountsFor<TMessage, TException>()
    {
        theSummaries.Any(x =>
                x.MessageType == typeof(TMessage).ToMessageTypeName() &&
                x.ExceptionType == typeof(TException).FullNameInCode())
            .ShouldBeFalse();
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

        theSummaries.ShouldContain(
            summaryCount<TargetMessage1, InvalidOperationException>(20, TransportConstants.DurableLocalUri));
        theSummaries.ShouldContain(
            summaryCount<TargetMessage1, DivideByZeroException>(5, TransportConstants.DurableLocalUri));
        theSummaries.ShouldContain(
            summaryCount<TargetMessage2, DivideByZeroException>(12, TransportConstants.DurableLocalUri));
        theSummaries.ShouldContain(summaryCount<TargetMessage2, BadImageFormatException>(14, new Uri("local://one")));
    }

    [Fact]
    public async Task get_summaries_after_a_time()
    {
        withTargetMessage1();
        theGenerator.ExceptionSource = msg => new InvalidOperationException(msg);
        await load(5, FiveHoursAgo);
        await load(7, FourHoursAgo);
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

        await fetchSummary(new TimeRange(FiveHoursAgo, null));

        theSummaries.ShouldContain(
            summaryCount<TargetMessage1, InvalidOperationException>(12, TransportConstants.DurableLocalUri));
        theSummaries.ShouldContain(
            summaryCount<TargetMessage1, DivideByZeroException>(3, TransportConstants.DurableLocalUri));
        theSummaries.ShouldContain(
            summaryCount<TargetMessage2, DivideByZeroException>(5, TransportConstants.DurableLocalUri));
    }

    [Fact]
    public async Task get_summaries_before_a_time()
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
        await load(10, FiveHoursAgo);
        await load(8, FourHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);

        theGenerator.ReceivedAt = new Uri("local://one");
        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(11, EightHoursAgo);
        await load(3, SevenHoursAgo);

        await fetchSummary(new TimeRange(null, SixHoursAgo.AddMinutes(-1)));

        noCountsFor<TargetMessage2, DivideByZeroException>();

        theSummaries.ShouldContain(
            summaryCount<TargetMessage1, InvalidOperationException>(8, TransportConstants.DurableLocalUri));
        theSummaries.ShouldContain(summaryCount<TargetMessage2, BadImageFormatException>(14, new Uri("local://one")));
    }


    [Fact]
    public async Task get_summaries_within_a_time_range()
    {
        withTargetMessage1();
        theGenerator.ExceptionSource = msg => new InvalidOperationException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);
        await load(8, SevenHoursAgo);
        await load(11, EightHoursAgo);

        await fetchSummary(new TimeRange(SevenHoursAgo, SixHoursAgo.AddMinutes(-1)));

        theSummaries.ShouldContain(summaryCount<TargetMessage1, InvalidOperationException>(8));
    }

    protected async Task loadAllEnvelopes()
    {
        allEnvelopes =
            await theDeadLetters.QueryAsync(new DeadLetterEnvelopeQuery(TimeRange.AllTime()) { PageSize = 0 },
                CancellationToken.None);
    }

    protected async Task queryMatches(DeadLetterEnvelopeQuery query, Func<DeadLetterEnvelope, bool> filter)
    {
        query.PageSize = 1000;
        var actual = await theDeadLetters.QueryAsync(query, CancellationToken.None);
        var expected = allEnvelopes.Envelopes.Where(filter).OrderBy(x => x.Id).ToList();

        //actual.TotalCount.ShouldBe(expected.Count);

        actual.Envelopes.Select(x => x.Id).OrderBy(x => x).ToArray()
            .ShouldBe(expected.Select(x => x.Id).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task query_for_envelopes_big_options()
    {
        withTargetMessage1();
        theGenerator.ExceptionSource = msg => new InvalidOperationException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);
        await load(8, SevenHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(3, FiveHoursAgo);
        await load(2, SixHoursAgo);

        theGenerator.ReceivedAt = new Uri("local://two");
        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(10, FiveHoursAgo);
        await load(8, FourHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);

        theGenerator.ReceivedAt = new Uri("local://one");
        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(11, EightHoursAgo);
        await load(3, SevenHoursAgo);

        withTargetMessage3();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(56, FiveHoursAgo);
        await load(45, FourHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(10, FiveHoursAgo);
        await load(13, FourHoursAgo);

        await loadAllEnvelopes();

        await queryMatches(new DeadLetterEnvelopeQuery(new TimeRange(SixHoursAgo, null)), e => e.SentAt >= SixHoursAgo);
        await queryMatches(new DeadLetterEnvelopeQuery(new TimeRange(null, SevenHoursAgo)),
            e => e.SentAt <= SevenHoursAgo);
        await queryMatches(new DeadLetterEnvelopeQuery(new TimeRange(SixHoursAgo, SevenHoursAgo)),
            e => e.SentAt >= SixHoursAgo && e.SentAt <= SevenHoursAgo);

        await queryMatches(
            new DeadLetterEnvelopeQuery(TimeRange.AllTime())
                { ExceptionType = typeof(BadImageFormatException).FullNameInCode() },
            e => e.ExceptionType == typeof(BadImageFormatException).FullNameInCode());

        await queryMatches(
            new DeadLetterEnvelopeQuery(TimeRange.AllTime())
                { MessageType = typeof(TargetMessage1).ToMessageTypeName() },
            e => e.MessageType == typeof(TargetMessage1).ToMessageTypeName());

        var receivedAtOne = new Uri("local://one").ToString();
        await queryMatches(new DeadLetterEnvelopeQuery(TimeRange.AllTime()) { ReceivedAt = receivedAtOne },
            e => e.ReceivedAt == receivedAtOne);
    }

    [Fact]
    public async Task paging_and_totals()
    {
        withTargetMessage1();
        theGenerator.ExceptionSource = msg => new InvalidOperationException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);
        await load(8, SevenHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(3, FiveHoursAgo);
        await load(2, SixHoursAgo);

        theGenerator.ReceivedAt = new Uri("local://two");
        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(10, FiveHoursAgo);
        await load(8, FourHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);

        theGenerator.ReceivedAt = new Uri("local://one");
        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(11, EightHoursAgo);
        await load(3, SevenHoursAgo);

        withTargetMessage3();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(56, FiveHoursAgo);
        await load(45, FourHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(10, FiveHoursAgo);
        await load(13, FourHoursAgo);

        await loadAllEnvelopes();

        var firstPage = await theDeadLetters.QueryAsync(
            new DeadLetterEnvelopeQuery(TimeRange.AllTime()) { PageSize = 10, PageNumber = 1 }, CancellationToken.None);

        firstPage.TotalCount.ShouldBe(allEnvelopes.TotalCount);
        firstPage.PageNumber.ShouldBe(1);
        firstPage.Envelopes.Count.ShouldBe(10);

        var secondPage = await theDeadLetters.QueryAsync(
            new DeadLetterEnvelopeQuery(TimeRange.AllTime()) { PageSize = 10, PageNumber = 2 }, CancellationToken.None);

        secondPage.TotalCount.ShouldBe(allEnvelopes.TotalCount);
        secondPage.PageNumber.ShouldBe(2);
        secondPage.Envelopes.Count.ShouldBe(10);
    }

    [Fact]
    public async Task discard_by_query()
    {
        withTargetMessage1();
        theGenerator.ExceptionSource = msg => new InvalidOperationException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);
        await load(8, SevenHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(3, FiveHoursAgo);
        await load(2, SixHoursAgo);

        theGenerator.ReceivedAt = new Uri("local://two");
        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(10, FiveHoursAgo);
        await load(8, FourHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);

        theGenerator.ReceivedAt = new Uri("local://one");
        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(11, EightHoursAgo);
        await load(3, SevenHoursAgo);

        withTargetMessage3();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(56, FiveHoursAgo);
        await load(45, FourHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(10, FiveHoursAgo);
        await load(13, FourHoursAgo);

        var query = new DeadLetterEnvelopeQuery(TimeRange.AllTime())
            { ExceptionType = typeof(BadImageFormatException).FullNameInCode() };
        await theDeadLetters.DiscardAsync(
            query, CancellationToken.None);

        var results = await theDeadLetters.QueryAsync(query, CancellationToken.None);
        results.TotalCount.ShouldBe(0);
        results.Envelopes.Count.ShouldBe(0);
    }

    [Fact]
    public async Task replay_by_query()
    {
        withTargetMessage1();
        theGenerator.ExceptionSource = msg => new InvalidOperationException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);
        await load(8, SevenHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(3, FiveHoursAgo);
        await load(2, SixHoursAgo);

        theGenerator.ReceivedAt = new Uri("local://two");
        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(10, FiveHoursAgo);
        await load(8, FourHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);

        theGenerator.ReceivedAt = new Uri("local://one");
        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(11, EightHoursAgo);
        await load(3, SevenHoursAgo);

        withTargetMessage3();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(56, FiveHoursAgo);
        await load(45, FourHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(10, FiveHoursAgo);
        await load(13, FourHoursAgo);

        var query = new DeadLetterEnvelopeQuery(TimeRange.AllTime())
            { ExceptionType = typeof(BadImageFormatException).FullNameInCode() };
        await theDeadLetters.ReplayAsync(
            query, CancellationToken.None);

        var results = await theDeadLetters.QueryAsync(query, CancellationToken.None);
        results.TotalCount.ShouldBe(0);
        results.Envelopes.Count.ShouldBe(0);
    }

    [Fact]
    public async Task discard_by_message_batch()
    {
        withTargetMessage1();
        theGenerator.ExceptionSource = msg => new InvalidOperationException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);
        await load(8, SevenHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(3, FiveHoursAgo);
        await load(2, SixHoursAgo);

        theGenerator.ReceivedAt = new Uri("local://two");
        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(10, FiveHoursAgo);
        await load(8, FourHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);

        theGenerator.ReceivedAt = new Uri("local://one");
        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(11, EightHoursAgo);
        await load(3, SevenHoursAgo);

        withTargetMessage3();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(56, FiveHoursAgo);
        await load(45, FourHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(10, FiveHoursAgo);
        await load(13, FourHoursAgo);

        await loadAllEnvelopes();

        var ids = allEnvelopes.Envelopes.Take(10).Select(x => x.Id).ToArray();
        await theDeadLetters.DiscardAsync(new MessageBatchRequest(ids), CancellationToken.None);

        // Reload
        await loadAllEnvelopes();
        allEnvelopes.Envelopes.Where(x => ids.Contains(x.Id)).Any().ShouldBeFalse();
    }

    [Fact]
    public async Task replay_by_message_batch()
    {
        withTargetMessage1();
        theGenerator.ExceptionSource = msg => new InvalidOperationException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);
        await load(8, SevenHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(3, FiveHoursAgo);
        await load(2, SixHoursAgo);

        theGenerator.ReceivedAt = new Uri("local://two");
        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(10, FiveHoursAgo);
        await load(8, FourHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(5, FiveHoursAgo);
        await load(7, SixHoursAgo);

        theGenerator.ReceivedAt = new Uri("local://one");
        withTargetMessage2();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(11, EightHoursAgo);
        await load(3, SevenHoursAgo);

        withTargetMessage3();
        theGenerator.ExceptionSource = msg => new BadImageFormatException(msg);
        await load(56, FiveHoursAgo);
        await load(45, FourHoursAgo);

        theGenerator.ExceptionSource = msg => new DivideByZeroException(msg);
        await load(10, FiveHoursAgo);
        await load(13, FourHoursAgo);

        await loadAllEnvelopes();

        var ids = allEnvelopes.Envelopes.Take(10).Select(x => x.Id).ToArray();
        await theDeadLetters.ReplayAsync(new MessageBatchRequest(ids), CancellationToken.None);

        // Reload
        await loadAllEnvelopes();
        allEnvelopes.Envelopes.Where(x => ids.Contains(x.Id)).Any().ShouldBeFalse();
    }
}

public record TargetMessage1(Guid Id, int Number, string Color);

public record TargetMessage2(Guid Id, int Number, string Color);

public record TargetMessage3(Guid Id, int Number, string Color);