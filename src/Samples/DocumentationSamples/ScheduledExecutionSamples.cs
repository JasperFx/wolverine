using JasperFx.Core;
using Wolverine;

namespace DocumentationSamples;

public class ScheduledExecutionSamples
{
    #region sample_ScheduleSend_In_3_Days

    public async Task schedule_send(IMessageContext context, Guid issueId)
    {
        var timeout = new WarnIfIssueIsStale
        {
            IssueId = issueId
        };

        // Process the issue timeout logic 3 days from now
        await context.ScheduleAsync(timeout, 3.Days());
    }

    #endregion

    #region sample_ScheduleSend_At_5_PM_Tomorrow

    public async Task schedule_send_at_5_tomorrow_afternoon(IMessageContext context, Guid issueId)
    {
        var timeout = new WarnIfIssueIsStale
        {
            IssueId = issueId
        };

        var time = DateTime.Today.AddDays(1).AddHours(17);


        // Process the issue timeout at 5PM tomorrow
        // Do note that Wolverine quietly converts this
        // to universal time in storage
        await context.ScheduleAsync(timeout, time);
    }

    #endregion


    #region sample_ScheduleLocally_In_3_Days

    public async Task schedule_locally(IMessageContext context, Guid issueId)
    {
        var timeout = new WarnIfIssueIsStale
        {
            IssueId = issueId
        };

        // Process the issue timeout logic 3 days from now
        // in *this* system
        await context.ScheduleAsync(timeout, 3.Days());
    }

    #endregion


    #region sample_ScheduleLocally_At_5_PM_Tomorrow

    public async Task schedule_locally_at_5_tomorrow_afternoon(IMessageContext context, Guid issueId)
    {
        var timeout = new WarnIfIssueIsStale
        {
            IssueId = issueId
        };

        var time = DateTime.Today.AddDays(1).AddHours(17);


        // Process the issue timeout at 5PM tomorrow
        // in *this* system
        // Do note that Wolverine quietly converts this
        // to universal time in storage
        await context.ScheduleAsync(timeout, time);
    }

    #endregion
}

public class WarnIfIssueIsStale
{
    public Guid IssueId { get; set; }
}