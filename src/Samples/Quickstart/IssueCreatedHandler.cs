using System.Net.Mail;

namespace Quickstart;

#region sample_Quickstart_IssueCreatedHandler

public static class IssueCreatedHandler
{
    public static async Task Handle(IssueCreated created, IssueRepository repository)
    {
        var issue = repository.Get(created.Id);
        var message = await BuildEmailMessage(issue);
        using var client = new SmtpClient();
        client.Send(message);
    }

    // This is a little helper method I made public
    // Wolverine will not expose this as a message handler
    internal static Task<MailMessage> BuildEmailMessage(Issue issue)
    {
        // Build up a templated email message, with
        // some sort of async method to look up additional
        // data just so we can show off an async
        // Wolverine Handler
        return Task.FromResult(new MailMessage());
    }
}

#endregion

public class AssignUserHandler
{
    public IssueAssigned Handle(AssignIssue command, IssueRepository issues)
    {
        var issue = issues.Get(command.IssueId);
        issue.AssigneeId = command.AssigneeId;
        issues.Store(issue); // it's all in memory, but just let this go...
        return new IssueAssigned(issue.Id);
    }
}

public static class UserAssignedHandler
{
    public static void Handle(IssueAssigned assigned, UserRepository users, IssueRepository issues)
    {
        var issue = issues.Get(assigned.Id);
        var user = users.Get(issue.AssigneeId.Value);
        var message = BuildEmailMessage(issue, user);
        using var client = new SmtpClient();
        client.SendAsync(message, "some token");
    }

    public static MailMessage BuildEmailMessage(Issue issue, User user)
    {
        // Build up a templated email message
        return new MailMessage();
    }
}