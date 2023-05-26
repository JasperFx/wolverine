using Wolverine;
using Wolverine.Attributes;

namespace DocumentationSamples;

#region sample_message_type_discovery

public record CreateIssue(string Name) : IMessage;

public record DeleteIssue(Guid Id) : ICommand;

public record IssueCreated(Guid Id, string Name) : IEvent;

#endregion

#region sample_using_WolverineMessage_attribute

[WolverineMessage]
public record CloseIssue(Guid Id);

#endregion