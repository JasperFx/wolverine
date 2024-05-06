using Wolverine.Attributes;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting;

[MessageIdentity("routed")]
public class RoutedMessage;