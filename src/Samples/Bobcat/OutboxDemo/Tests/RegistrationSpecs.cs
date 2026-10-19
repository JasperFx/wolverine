namespace OutboxDemo.Tests;

/// <summary>
/// The one endpoint this sample exposes: <c>POST /registration</c>, whose happy path returns 204 while
/// cascading a Registration saga and two messages through Marten's outbox in the same transaction, and
/// whose compound <c>ValidateAsync</c> handler rejects a duplicate registration with a 409.
/// </summary>
[BobcatFeature("Outbox Demo")]
public class RegistrationSpecs(OutboxDemoHost app) : OutboxDemoSpec(app)
{
    private int _lastStatusCode;
    private string? _lastProblemDetail;

    [BobcatSpec]
    public async Task a_new_registration_is_accepted()
    {
        await WhenARegistrationIsSubmitted("member-001", "event-001");

        ThenTheResponseStatusIs(204);
    }

    [BobcatSpec]
    public async Task the_same_member_cannot_register_twice_for_one_event()
    {
        await GivenARegistration("dup-member", "dup-event");

        await WhenARegistrationIsSubmitted("dup-member", "dup-event");

        ThenTheResponseStatusIs(409);
        ThenTheRejectionNamesTheDuplicateMember("dup-member");
    }

    [BobcatSpec]
    public async Task the_same_member_may_register_for_a_different_event()
    {
        await GivenARegistration("multi-member", "event-a");

        await WhenARegistrationIsSubmitted("multi-member", "event-b");

        ThenTheResponseStatusIs(204);
    }

    [BobcatSpec]
    public async Task the_payment_rides_along_with_the_registration()
    {
        await WhenARegistrationIsSubmittedPaying("paying-member", "event-002", 250);

        ThenTheResponseStatusIs(204);
    }

    // ---- steps ------------------------------------------------------------------------------

    [Given("a registration for member \"{memberId}\" at event \"{eventId}\"")]
    public Task GivenARegistration(string memberId, string eventId) => submit(memberId, eventId, 100m);

    [When("a registration is submitted for member \"{memberId}\" at event \"{eventId}\"")]
    public Task WhenARegistrationIsSubmitted(string memberId, string eventId) => submit(memberId, eventId, 100m);

    [When("a registration is submitted for member \"{memberId}\" at event \"{eventId}\" paying {payment}")]
    public Task WhenARegistrationIsSubmittedPaying(string memberId, string eventId, decimal payment)
        => submit(memberId, eventId, payment);

    private async Task submit(string memberId, string eventId, decimal payment)
    {
        // Tracked: the saga and the two messages it cascades are handled after the response goes out,
        // and a spec that left them in flight would have the next spec's reset delete the saga under them.
        var result = await TrackedAsync(() => PostJsonAsync<SubmitRegistration, ProblemDetailsBody>(
            "/registration", new SubmitRegistration(eventId, memberId, payment)));

        _lastStatusCode = result.StatusCode;
        _lastProblemDetail = result.Body?.Detail;
    }

    [Then("the response status is {status}")]
    public void ThenTheResponseStatusIs(int status) => SpecAssert.Check("status", _lastStatusCode, status);

    [Then("the rejection names the duplicate member \"{memberId}\"")]
    public void ThenTheRejectionNamesTheDuplicateMember(string memberId)
        => SpecAssert.Fact(_lastProblemDetail?.Contains(memberId, StringComparison.Ordinal) == true,
            $"The rejection reads '{_lastProblemDetail}', which does not name '{memberId}'.");
}

/// <summary>
/// Just enough of RFC 7807 to read the rejection back, so the spec project does not take an MVC
/// dependency to read one string.
/// </summary>
public record ProblemDetailsBody(string? Detail, int? Status);
