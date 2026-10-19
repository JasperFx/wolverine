using Administration;
using Meetings;
using Payments;
using Registrations;
using UserAccess;

namespace MeetingGroupMonolith.Tests;

/// <summary>
/// The modular-monolith-with-ddd conversion: five modules in one process that talk to each other only
/// through durable local queues. Registering a user makes the Meetings module create a member; accepting
/// a proposal makes it create the group; starting a subscription in Payments pushes a new expiration date
/// back onto the member in Meetings. None of those is a direct call, so these specs are as much about the
/// cascade arriving as about the endpoint returning -- every cascading write is tracked until it is
/// handled.
/// </summary>
/// <remarks>
/// The Payments module is event-sourced, so the subscription and attendee specs also assert the events
/// their request appended -- the attendee's fee stream is started by a cascade, and WhenActing reads it
/// back because the tracked request does not return until that cascade is handled.
/// </remarks>
[BobcatFeature("Meeting Group Monolith")]
public class MeetingGroupSpecs(MeetingGroupMonolithHost app) : MeetingGroupMonolithSpec(app)
{
    private Guid _userId;
    private Guid _proposalId;
    private Guid _meetingId;
    private Guid _subscriptionId;
    private int _lastStatusCode;
    private List<MeetingGroup> _meetingGroups = [];
    private List<Meeting> _meetings = [];

    [BobcatSpec]
    public async Task register_a_user()
    {
        await WhenAUserRegisters("user@example.com", "Password1!");

        ThenTheResponseStatusIs(200);
        await ThenAMemberExistsInTheMeetingsModule();
    }

    [BobcatSpec]
    public async Task register_with_invalid_data_returns_400()
    {
        await WhenAUserRegisters("", "Password1!");

        ThenTheResponseStatusIs(400);
    }

    [BobcatSpec]
    public async Task propose_a_meeting_group()
    {
        await GivenARegisteredUser("proposer@example.com", "Password1!");

        await WhenAMeetingGroupIsProposed("DDD Group", "Warsaw", "PL");

        ThenTheResponseStatusIs(201);
        await ThenTheProposalIs(ProposalStatus.InVerification);
    }

    [BobcatSpec]
    public async Task propose_a_meeting_group_without_a_city_returns_400()
    {
        await GivenARegisteredUser("lost@example.com", "Password1!");

        await WhenAMeetingGroupIsProposed("Nowhere Group", "", "PL");

        ThenTheResponseStatusIs(400);
    }

    [BobcatSpec]
    public async Task accept_a_meeting_group_proposal()
    {
        await GivenARegisteredUser("accepter@example.com", "Password1!");
        await GivenAProposedMeetingGroup("Clean Code Group", "Krakow", "PL");

        await WhenTheProposalIsAccepted();

        ThenTheResponseStatusIs(200);
        await ThenTheProposalIs(ProposalStatus.Accepted);
        await ThenAMeetingGroupExistsForTheProposal("Clean Code Group");
        await ThenTheProposerIsAnOrganizerOfTheGroup();
    }

    [BobcatSpec]
    public async Task create_a_meeting()
    {
        await GivenARegisteredUser("organizer@example.com", "Password1!");
        await GivenAProposedMeetingGroup("Agile Group", "Gdansk", "PL");
        await GivenTheProposalWasAccepted();

        await WhenAMeetingIsCreated("First Meetup");

        ThenTheResponseStatusIs(201);
        await ThenTheMeetingBelongsToTheGroup("First Meetup");
    }

    [BobcatSpec]
    public async Task add_an_attendee()
    {
        await GivenARegisteredUser("attendee@example.com", "Password1!");
        await GivenAProposedMeetingGroup("DevOps Group", "Poznan", "PL");
        await GivenTheProposalWasAccepted();
        await GivenAMeeting("DevOps Meetup");

        await WhenTheUserIsAddedAsAnAttendee();

        ThenTheResponseStatusIs(200);
        await ThenTheMeetingHasAttendees(1);
        ThenEvents(Specify<MeetingFeeCreated>().With(x => x.PayerId, _userId).With(x => x.MeetingId, _meetingId));
    }

    [BobcatSpec]
    public async Task adding_the_same_attendee_twice_returns_409()
    {
        await GivenARegisteredUser("eager@example.com", "Password1!");
        await GivenAProposedMeetingGroup("Eager Group", "Katowice", "PL");
        await GivenTheProposalWasAccepted();
        await GivenAMeeting("Eager Meetup");
        await GivenTheUserWasAddedAsAnAttendee();

        await WhenTheUserIsAddedAsAnAttendee();

        ThenTheResponseStatusIs(409);
        await ThenTheMeetingHasAttendees(1);
        ThenNoEvents();
    }

    [BobcatSpec]
    public async Task get_meeting_groups()
    {
        await GivenARegisteredUser("viewer@example.com", "Password1!");
        await GivenAProposedMeetingGroup("Viewer Group", "Lodz", "PL");
        await GivenTheProposalWasAccepted();

        await WhenAllMeetingGroupsAreRead();

        ThenTheMeetingGroupIsListed("Viewer Group");
    }

    [BobcatSpec]
    public async Task get_meetings_for_a_group()
    {
        await GivenARegisteredUser("meeting.viewer@example.com", "Password1!");
        await GivenAProposedMeetingGroup("Meeting Viewer Group", "Wroclaw", "PL");
        await GivenTheProposalWasAccepted();
        await GivenAMeeting("Visible Meeting");

        await WhenAllMeetingsForTheGroupAreRead();

        ThenTheMeetingIsListed("Visible Meeting");
    }

    [BobcatSpec]
    public async Task create_subscription()
    {
        await GivenARegisteredUser("subscriber@example.com", "Password1!");

        await WhenASubscriptionIsCreated("Monthly");

        ThenTheResponseStatusIs(201);
        ThenEvents(Specify<SubscriptionCreated>().With(x => x.PayerId, _userId).With(x => x.Period, "Monthly"));
        await ThenAggregate<Subscription>(TheStartedStream<Subscription>(),
            Specify<Subscription>().With(x => x.Status, SubscriptionStatus.Active));
        await ThenTheSubscriptionIsActive();
        await ThenTheMembersSubscriptionExpirationIsInTheFuture();
    }

    [BobcatSpec]
    public async Task create_subscription_with_an_unknown_period_returns_400()
    {
        await GivenARegisteredUser("badsub@example.com", "Password1!");

        await WhenASubscriptionIsCreated("Weekly");

        ThenTheResponseStatusIs(400);
        ThenNoEvents();
    }

    // ---- registration -----------------------------------------------------------------------
    //
    // A When step goes through WhenActing, so what the request appended (cascades included) is what
    // ThenEvents and ThenNoEvents judge. A Given step is a plain call: it arranges, it is not the act.

    [Given("a user registered with email \"{email}\" and password \"{password}\"")]
    public Task GivenARegisteredUser(string email, string password) => register(email, password);

    public Task WhenAUserRegisters(string email, string password)
        => WhenActing($"a user registers with email \"{email}\" and password \"{password}\"", _ => register(email, password));

    private async Task register(string email, string password)
    {
        // The login is the part of the email before the @, and the names are filler the validator insists
        // on but no spec cares about.
        var login = email.Split('@')[0];

        var result = await TrackedAsync(() => PostJsonAsync<RegisterUser, User>(
            "/api/registrations", new RegisterUser(login, email, "Test", "User", password)));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _userId = result.Body.Id;
    }

    // ---- proposals --------------------------------------------------------------------------

    [Given("a meeting group named \"{name}\" proposed in \"{city}\", \"{countryCode}\"")]
    public Task GivenAProposedMeetingGroup(string name, string city, string countryCode) => propose(name, city, countryCode);

    public Task WhenAMeetingGroupIsProposed(string name, string city, string countryCode)
        => WhenActing($"a meeting group named \"{name}\" is proposed in \"{city}\", \"{countryCode}\"",
            _ => propose(name, city, countryCode));

    private async Task propose(string name, string city, string countryCode)
    {
        var result = await PostJsonAsync<ProposeNewMeetingGroup, ProposalCreation>(
            "/api/administration/proposals",
            new ProposeNewMeetingGroup(name, $"A group about {name}", city, countryCode, _userId));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _proposalId = result.Body.Id;
    }

    [Given("the meeting group proposal was accepted")]
    public Task GivenTheProposalWasAccepted() => accept();

    public Task WhenTheProposalIsAccepted() => WhenActing("the meeting group proposal is accepted", _ => accept());

    private async Task accept()
    {
        // Accepting cascades MeetingGroupProposalAcceptedEvent to the Meetings module, which creates the
        // group under the proposal's id.
        var result = await TrackedAsync(() => PostJsonAsync<object, MeetingGroupProposal>(
            $"/api/administration/proposals/{_proposalId}/accept", new { }));

        _lastStatusCode = result.StatusCode;
    }

    // ---- meetings ---------------------------------------------------------------------------

    [Given("a meeting named \"{title}\" in the group")]
    public Task GivenAMeeting(string title) => createMeeting(title);

    public Task WhenAMeetingIsCreated(string title)
        => WhenActing($"a meeting named \"{title}\" is created in the group", _ => createMeeting(title));

    private async Task createMeeting(string title)
    {
        var start = DateTime.UtcNow.AddDays(7);
        var result = await PostJsonAsync<CreateMeeting, MeetingCreation>(
            "/api/meetings",
            new CreateMeeting(_proposalId, title, $"About {title}", start, start.AddHours(2),
                "Main Street 1", AttendeesLimit: null, Fee: 0m));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _meetingId = result.Body.Id;
    }

    [Given("the user was added as an attendee")]
    public Task GivenTheUserWasAddedAsAnAttendee() => addAttendee();

    public Task WhenTheUserIsAddedAsAnAttendee() => WhenActing("the user is added as an attendee", _ => addAttendee());

    private async Task addAttendee()
    {
        // Cascades MeetingAttendeeAddedEvent to the Payments module, which starts a fee stream.
        var result = await TrackedAsync(() => PostJsonAsync<AddAttendee, Meeting>(
            $"/api/meetings/{_meetingId}/attendees", new AddAttendee(_meetingId, _userId)));

        _lastStatusCode = result.StatusCode;
    }

    // ---- queries ----------------------------------------------------------------------------

    public Task WhenAllMeetingGroupsAreRead() => WhenActing("all meeting groups are read", async _ =>
    {
        var result = await GetJsonAsync<List<MeetingGroup>>("/api/meeting-groups");
        _lastStatusCode = result.StatusCode;
        _meetingGroups = result.Body ?? [];
    });

    public Task WhenAllMeetingsForTheGroupAreRead() => WhenActing("all meetings for the group are read", async _ =>
    {
        var result = await GetJsonAsync<List<Meeting>>($"/api/meeting-groups/{_proposalId}/meetings");
        _lastStatusCode = result.StatusCode;
        _meetings = result.Body ?? [];
    });

    // ---- subscriptions ----------------------------------------------------------------------

    public Task WhenASubscriptionIsCreated(string period) => WhenActing($"a {period} subscription is created", async _ =>
    {
        // Cascades SubscriptionExpirationChangedEvent back to the Meetings module, which stamps the new
        // expiration date on the Member.
        var result = await TrackedAsync(() => PostJsonAsync<CreateSubscription, SubscriptionCreation>(
            "/api/payments/subscriptions", new CreateSubscription(_userId, period)));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _subscriptionId = result.Body.Id;
    });

    // ---- assertions -------------------------------------------------------------------------

    [Then("the response status is {status}")]
    public void ThenTheResponseStatusIs(int status) => SpecAssert.Check("status", _lastStatusCode, status);

    [Then("a member exists in the Meetings module for that user")]
    public async Task ThenAMemberExistsInTheMeetingsModule()
    {
        var result = await GetJsonAsync<Member>($"/api/members/{_userId}");
        SpecAssert.Check("status", result.StatusCode, 200);
        SpecAssert.Check("id", result.Body?.Id, _userId);
    }

    [Then("the proposal is {status}")]
    public async Task ThenTheProposalIs(ProposalStatus status)
        => SpecAssert.Check("status",
            (await GetJsonAsync<MeetingGroupProposal>($"/api/administration/proposals/{_proposalId}")).Body?.Status, status);

    [Then("a meeting group named \"{name}\" exists for the proposal")]
    public async Task ThenAMeetingGroupExistsForTheProposal(string name)
        => SpecAssert.Check("name", (await GetJsonAsync<MeetingGroup>($"/api/meeting-groups/{_proposalId}")).Body?.Name, name);

    [Then("the proposer is an organizer of the group")]
    public async Task ThenTheProposerIsAnOrganizerOfTheGroup()
    {
        var group = (await GetJsonAsync<MeetingGroup>($"/api/meeting-groups/{_proposalId}")).Body;
        SpecAssert.Fact(group?.Members.Any(m => m.MemberId == _userId && m.Role == "Organizer") == true,
            $"User {_userId} is not an organizer of the group.");
    }

    [Then("the meeting \"{title}\" belongs to the group")]
    public async Task ThenTheMeetingBelongsToTheGroup(string title)
    {
        var meeting = (await GetJsonAsync<Meeting>($"/api/meetings/{_meetingId}")).Body;
        SpecAssert.Check("title", meeting?.Title, title);
        SpecAssert.Check("group", meeting?.MeetingGroupId, _proposalId);
    }

    [Then("the meeting has {count} attendee")]
    public async Task ThenTheMeetingHasAttendees(int count)
        => SpecAssert.Check("count", (await GetJsonAsync<Meeting>($"/api/meetings/{_meetingId}")).Body?.Attendees.Count, count);

    [Then("the meeting group \"{name}\" is listed")]
    public void ThenTheMeetingGroupIsListed(string name)
        => SpecAssert.Fact(_meetingGroups.Any(g => g.Name == name),
            $"The groups are [{string.Join(", ", _meetingGroups.Select(g => g.Name))}], expected '{name}'.");

    [Then("the meeting \"{title}\" is listed")]
    public void ThenTheMeetingIsListed(string title)
        => SpecAssert.Fact(_meetings.Any(m => m.Title == title),
            $"The meetings are [{string.Join(", ", _meetings.Select(m => m.Title))}], expected '{title}'.");

    /// <summary>Reads the subscription back over HTTP: the GET serves the inline snapshot of the aggregate.</summary>
    [Then("the subscription is active")]
    public async Task ThenTheSubscriptionIsActive()
        => SpecAssert.Check("status",
            (await GetJsonAsync<Subscription>($"/api/payments/subscriptions/{_subscriptionId}")).Body?.Status,
            SubscriptionStatus.Active);

    [Then("the member's subscription expiration is in the future")]
    public async Task ThenTheMembersSubscriptionExpirationIsInTheFuture()
    {
        var member = (await GetJsonAsync<Member>($"/api/members/{_userId}")).Body;
        SpecAssert.Fact(member?.SubscriptionExpirationDate > DateTime.UtcNow,
            $"The member's subscription expires {member?.SubscriptionExpirationDate}, which is not in the future.");
    }
}
