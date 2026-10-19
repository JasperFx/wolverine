using Alba;
using Bobcat.CritterStack;
using Mentorships;
using Speakers;

namespace MoreSpeakers.Tests;

/// <summary>One HTTP call as a step sees it: the status, and the body if there was one.</summary>
public record HttpResult<T>(int StatusCode, T? Body);

/// <summary>
/// The base for every MoreSpeakers spec: the HTTP calls a step makes, over Alba, and an empty store before
/// every spec.
/// </summary>
/// <remarks>
/// The status is surfaced rather than asserted. Alba checks for a 200 unless told otherwise, and a spec
/// asserts whatever status it expects, so a 404 a spec is testing for must not fail the call that
/// produced it.
/// </remarks>
public abstract class MoreSpeakersSpec(MoreSpeakersHost app) : IAsyncLifetime
{
    protected IAlbaHost Alba => app.Host;

    // Load-bearing: POST /api/speakers refuses a duplicate email with a 409, and every spec registers speakers under fixed addresses. Without the reset the suite passes once per database and then every registration is a 409 for a speaker it believes is new.
    public async ValueTask InitializeAsync() => await app.Host.ResetEventStoresAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected Task<HttpResult<T>> PostJsonAsync<TRequest, T>(string url, TRequest body)
        => SendAsync<T>(s => s.Post.Json(body!).ToUrl(url));

    protected Task<HttpResult<T>> PutJsonAsync<TRequest, T>(string url, TRequest body)
        => SendAsync<T>(s => s.Put.Json(body!).ToUrl(url));

    protected Task<HttpResult<T>> GetJsonAsync<T>(string url)
        => SendAsync<T>(s => s.Get.Url(url));

    protected Task<HttpResult<object>> DeleteAsync(string url)
        => SendAsync<object>(s => s.Delete.Url(url));

    protected async Task<HttpResult<T>> SendAsync<T>(Action<Scenario> configure)
    {
        var result = await Alba.Scenario(s =>
        {
            configure(s);
            s.IgnoreStatusCode();
        });

        T? body = default;
        try { body = await result.ReadAsJsonAsync<T>(); } catch { /* not every response carries JSON */ }

        return new HttpResult<T>(result.Context.Response.StatusCode, body);
    }

    // ---- the MoreSpeakers vocabulary -------------------------------------------------------
    //
    // Shared by SpeakerSpecs and MentorshipSpecs. Reuses the host's own command and document types via the
    // project reference rather than re-declaring request records locally.

    private Speaker? _speaker;
    private Guid _speakerId;
    private Guid _mentorId;
    private Guid _mentorshipId;
    private int _lastStatusCode;
    private List<Speaker> _speakers = [];

    [Given("a speaker registered with email \"{email}\" and name \"{name}\"")]
    public Task GivenASpeaker(string email, string name) => registerSpeaker(email, name);

    [When("a speaker registers with email \"{email}\" and name \"{name}\"")]
    public Task WhenASpeakerRegisters(string email, string name) => registerSpeaker(email, name);

    private async Task registerSpeaker(string email, string name)
    {
        var (first, last) = splitName(name);
        var result = await PostJsonAsync<RegisterSpeaker, Speaker>(
            "/api/speakers", new RegisterSpeaker(email, first, last, SpeakerType.New));

        _lastStatusCode = result.StatusCode;

        // The first speaker a spec registers is "the speaker" every later step refers to; the
        // duplicate-email spec's second call is a 409 whose ProblemDetails body must not displace it.
        if (result.StatusCode == 201 && result.Body is not null && _speakerId == Guid.Empty)
        {
            _speaker = result.Body;
            _speakerId = result.Body.Id;
        }
    }

    /// <summary>
    /// A mentor is a speaker the host will let others request mentorship from, and
    /// <c>RequestMentorshipEndpoint.Validate</c> refuses with a 400 unless <c>IsAvailableForMentoring</c> is
    /// set. Registration has no such flag -- it is a profile attribute -- so registering a mentor is a POST
    /// followed by the PUT that opts them in, as an experienced speaker.
    /// </summary>
    [Given("a mentor registered with email \"{email}\" and name \"{name}\"")]
    public async Task GivenAMentor(string email, string name)
    {
        var (first, last) = splitName(name);
        var registered = await PostJsonAsync<RegisterSpeaker, Speaker>(
            "/api/speakers", new RegisterSpeaker(email, first, last, SpeakerType.Experienced));

        _lastStatusCode = registered.StatusCode;
        if (registered.Body is null) return;

        var mentor = registered.Body;
        var optIn = await PutJsonAsync<UpdateSpeakerProfile, Speaker>(
            $"/api/speakers/{mentor.Id}",
            profileFor(mentor) with { IsAvailableForMentoring = true, MaxMentees = 3 });

        _lastStatusCode = optIn.StatusCode;
        _mentorId = mentor.Id;
    }

    /// <summary>The counter-case: a speaker who exists but never opted in to mentoring.</summary>
    [Given("a speaker who is not mentoring registered with email \"{email}\" and name \"{name}\"")]
    public async Task GivenASpeakerWhoIsNotMentoring(string email, string name)
    {
        var (first, last) = splitName(name);
        var result = await PostJsonAsync<RegisterSpeaker, Speaker>(
            "/api/speakers", new RegisterSpeaker(email, first, last, SpeakerType.Experienced));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _mentorId = result.Body.Id;
    }

    [When("all speakers are read")]
    public async Task WhenAllSpeakersAreRead()
    {
        var result = await GetJsonAsync<List<Speaker>>("/api/speakers");
        _lastStatusCode = result.StatusCode;
        _speakers = result.Body ?? [];
    }

    [When("the speaker with id {id} is read")]
    public async Task WhenTheSpeakerIsRead(Guid id)
    {
        var result = await GetJsonAsync<Speaker>($"/api/speakers/{id}");
        _lastStatusCode = result.StatusCode;
    }

    [When("the speaker's bio is updated to \"{bio}\"")]
    public async Task WhenTheSpeakersBioIsUpdated(string bio)
    {
        // The command is the whole profile, whose validator requires first and last name, so the step
        // replays the speaker it registered with the one field changed.
        var result = await PutJsonAsync<UpdateSpeakerProfile, Speaker>(
            $"/api/speakers/{_speakerId}", profileFor(_speaker!) with { Bio = bio });

        _lastStatusCode = result.StatusCode;
    }

    [Given("the speaker has requested mentorship from the mentor")]
    public Task GivenMentorshipWasRequested() => requestMentorship(_mentorId);

    [When("the speaker requests mentorship from the mentor")]
    public Task WhenMentorshipIsRequested() => requestMentorship(_mentorId);

    [When("the speaker requests mentorship from themselves")]
    public Task WhenSelfMentorshipIsRequested() => requestMentorship(_speakerId);

    private async Task requestMentorship(Guid mentorId)
    {
        var result = await PostJsonAsync<RequestMentorship, Mentorship>(
            "/api/mentorships",
            new RequestMentorship(mentorId, _speakerId, MentorshipType.NewToExperienced,
                "Help me with my first talk", ["Distributed systems"], "Weekly"));

        _lastStatusCode = result.StatusCode;
        if (result.StatusCode == 201 && result.Body is not null) _mentorshipId = result.Body.Id;
    }

    [Given("the mentor has accepted the mentorship")]
    public Task GivenTheMentorAccepted() => acceptMentorship();

    [When("the mentor accepts the mentorship")]
    public Task WhenTheMentorAccepts() => acceptMentorship();

    private async Task acceptMentorship()
    {
        var result = await PostJsonAsync<AcceptMentorship, Mentorship>(
            $"/api/mentorships/{_mentorshipId}/accept", new AcceptMentorship(_mentorshipId, "Happy to help"));

        _lastStatusCode = result.StatusCode;
    }

    [When("the mentor completes the mentorship")]
    public async Task WhenTheMentorCompletes()
    {
        var result = await PostJsonAsync<CompleteMentorship, Mentorship>(
            $"/api/mentorships/{_mentorshipId}/complete", new CompleteMentorship(_mentorshipId));

        _lastStatusCode = result.StatusCode;
    }

    [Then("the response status is {status}")]
    public void ThenTheResponseStatusIs(int status) => SpecAssert.Check("status", _lastStatusCode, status);

    [Then("the speaker id is returned")]
    public void ThenTheSpeakerIdIsReturned() => SpecAssert.Fact(_speakerId != Guid.Empty, "No speaker id came back.");

    [Then("the mentorship id is returned")]
    public void ThenTheMentorshipIdIsReturned() => SpecAssert.Fact(_mentorshipId != Guid.Empty, "No mentorship id came back.");

    [Then("at least {count} speaker is returned")]
    public void ThenAtLeastSpeakersAreReturned(int count)
        => SpecAssert.Fact(_speakers.Count >= count, $"Expected at least {count} speaker(s), got {_speakers.Count}.");

    [Then("the speaker list contains \"{email}\"")]
    public void ThenTheSpeakerListContains(string email)
        => SpecAssert.Fact(_speakers.Any(s => s.Email == email),
            $"The speakers are [{string.Join(", ", _speakers.Select(s => s.Email))}], expected '{email}'.");

    /// <summary>
    /// Reads the speaker back rather than trusting the write's response body -- the POST echoes what it
    /// was about to store, and a sample whose point is "Marten stores the document" should show the
    /// document came back out.
    /// </summary>
    [Then("the stored speaker is named \"{name}\" with email \"{email}\"")]
    public async Task ThenTheStoredSpeakerIs(string name, string email)
    {
        var speaker = (await GetJsonAsync<Speaker>($"/api/speakers/{_speakerId}")).Body;
        SpecAssert.Check("name", speaker?.FullName, name);
        SpecAssert.Check("email", speaker?.Email, email);
    }

    [Then("the stored speaker's bio is \"{bio}\"")]
    public async Task ThenTheStoredSpeakersBioIs(string bio)
        => SpecAssert.Check("bio", (await GetJsonAsync<Speaker>($"/api/speakers/{_speakerId}")).Body?.Bio, bio);

    [Then("the stored mentorship status is {status}")]
    public async Task ThenTheStoredMentorshipStatusIs(string status)
        => SpecAssert.Check("status",
            (await GetJsonAsync<Mentorship>($"/api/mentorships/{_mentorshipId}")).Body?.Status.ToString(), status);

    /// <summary>A speaker's name as one string ("Alice Speaker"): first word first, the rest last.</summary>
    private static (string First, string Last) splitName(string name)
    {
        var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (name, name);
    }

    /// <summary>The full-profile PUT for a speaker as stored, so a step can change one field with <c>with</c>.</summary>
    private static UpdateSpeakerProfile profileFor(Speaker speaker) => new(
        speaker.Id, speaker.FirstName, speaker.LastName, speaker.Bio, speaker.Goals, speaker.HeadshotUrl,
        speaker.SessionizeUrl, speaker.IsAvailableForMentoring, speaker.MaxMentees, speaker.MentorshipFocus,
        speaker.Expertise, speaker.SocialLinks);
}
