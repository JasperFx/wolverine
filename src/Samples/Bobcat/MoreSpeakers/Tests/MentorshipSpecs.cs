namespace MoreSpeakers.Tests;

/// <summary>The mentorship lifecycle -- requested, accepted, completed -- and the requests the host refuses.</summary>
[BobcatFeature("More Speakers: mentorships")]
public class MentorshipSpecs(MoreSpeakersHost app) : MoreSpeakersSpec(app)
{
    [BobcatSpec]
    public async Task request_mentorship()
    {
        await GivenASpeaker("mentee@conf.com", "Mentee Speaker");
        await GivenAMentor("mentor@conf.com", "Mentor Speaker");

        await WhenMentorshipIsRequested();

        ThenTheResponseStatusIs(201);
        ThenTheMentorshipIdIsReturned();
        await ThenTheStoredMentorshipStatusIs("Pending");
    }

    [BobcatSpec]
    public async Task self_mentorship_returns_400()
    {
        await GivenASpeaker("self.mentor@conf.com", "Self Mentor");

        await WhenSelfMentorshipIsRequested();

        ThenTheResponseStatusIs(400);
    }

    [BobcatSpec]
    public async Task request_mentorship_from_a_speaker_who_is_not_mentoring_returns_400()
    {
        await GivenASpeaker("unavailable.mentee@conf.com", "Unavailable Mentee");
        await GivenASpeakerWhoIsNotMentoring("unavailable.mentor@conf.com", "Unavailable Mentor");

        await WhenMentorshipIsRequested();

        ThenTheResponseStatusIs(400);
    }

    [BobcatSpec]
    public async Task accept_mentorship()
    {
        await GivenASpeaker("accept.mentee@conf.com", "Accept Mentee");
        await GivenAMentor("accept.mentor@conf.com", "Accept Mentor");
        await GivenMentorshipWasRequested();

        await WhenTheMentorAccepts();

        ThenTheResponseStatusIs(200);
        await ThenTheStoredMentorshipStatusIs("Active");
    }

    [BobcatSpec]
    public async Task accept_non_pending_mentorship_returns_400()
    {
        await GivenASpeaker("nonpending.mentee@conf.com", "NonPending Mentee");
        await GivenAMentor("nonpending.mentor@conf.com", "NonPending Mentor");
        await GivenMentorshipWasRequested();
        await GivenTheMentorAccepted();

        await WhenTheMentorAccepts();

        ThenTheResponseStatusIs(400);
    }

    [BobcatSpec]
    public async Task complete_mentorship()
    {
        await GivenASpeaker("complete.mentee@conf.com", "Complete Mentee");
        await GivenAMentor("complete.mentor@conf.com", "Complete Mentor");
        await GivenMentorshipWasRequested();
        await GivenTheMentorAccepted();

        await WhenTheMentorCompletes();

        ThenTheResponseStatusIs(200);
        await ThenTheStoredMentorshipStatusIs("Completed");
    }
}
