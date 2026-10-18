namespace MoreSpeakers.Tests;

/// <summary>Speaker registration and profiles, driven over HTTP.</summary>
[BobcatFeature("More Speakers: speakers")]
public class SpeakerSpecs(MoreSpeakersHost app) : MoreSpeakersSpec(app)
{
    [BobcatSpec]
    public async Task register_a_speaker()
    {
        await WhenASpeakerRegisters("speaker@conf.com", "Alice Speaker");

        ThenTheResponseStatusIs(201);
        ThenTheSpeakerIdIsReturned();
        await ThenTheStoredSpeakerIs("Alice Speaker", "speaker@conf.com");
    }

    [BobcatSpec]
    public async Task register_duplicate_email_returns_409()
    {
        await GivenASpeaker("dup.speaker@conf.com", "Dup Speaker");

        await WhenASpeakerRegisters("dup.speaker@conf.com", "Dup Speaker Again");

        ThenTheResponseStatusIs(409);
    }

    [BobcatSpec]
    public async Task get_all_speakers()
    {
        await GivenASpeaker("list.speaker@conf.com", "Listed Speaker");

        await WhenAllSpeakersAreRead();

        ThenAtLeastSpeakersAreReturned(1);
        ThenTheSpeakerListContains("list.speaker@conf.com");
    }

    [BobcatSpec]
    public async Task get_speaker_by_id_returns_404_for_missing()
    {
        await WhenTheSpeakerIsRead(Guid.Empty);

        ThenTheResponseStatusIs(404);
    }

    [BobcatSpec]
    public async Task update_speaker_profile()
    {
        await GivenASpeaker("update.speaker@conf.com", "Update Speaker");

        await WhenTheSpeakersBioIsUpdated("Expert in distributed systems");

        ThenTheResponseStatusIs(200);
        await ThenTheStoredSpeakersBioIs("Expert in distributed systems");
    }
}
