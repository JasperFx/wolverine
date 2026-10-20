namespace Wolverine.Bobcat.Http.Tests;

// wolverine#5077: a timeout an HTTP endpoint schedules is what the act caused, not the act's own message, so it never
// trips the only-scheduled guard. It still only runs when the spec asks
[Collection(nameof(AppCollection))]
public class scheduled_by_an_endpoint(AppHost app) : WolverineHttpSpec(app.Host)
{
    [Fact]
    public async Task a_timeout_an_endpoint_scheduled_inside_when_tracked_is_not_the_acts_own()
    {
        var id = Guid.NewGuid();
        await WhenTracked("a reminder is requested over HTTP", () => AlbaHost.Scenario(x =>
        {
            x.Post.Url($"/api/appointments/{id}/remind-later");
            x.StatusCodeShouldBeSuccess();
        }));

        LastAct.OnlyScheduled.ShouldBeEmpty();
        LastAct.OwnMessages.ShouldBeEmpty();
        ThenMessageScheduled(new ReminderDue(id));

        // Nothing ran and nothing was expected to: a legitimate pass, not a vacuous one
        ThenNoMessageSent<ReminderSent>();

        await WhenScheduledMessagesAreExecuted();
        ThenMessageSent(new ReminderSent(id));
    }

    [Fact]
    public async Task a_timeout_a_body_less_act_scheduled_is_not_the_acts_own_either()
    {
        var id = Guid.NewGuid();
        await WhenPostedTo("/api/appointments/{id}/remind-later", id);

        LastAct.OnlyScheduled.ShouldBeEmpty();
        ThenMessageScheduled<ReminderDue>();
        ThenNoMessageSent<ReminderSent>();
        ThenNoEvents();
    }
}
