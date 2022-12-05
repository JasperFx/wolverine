using Wolverine.Attributes;

namespace Ponger;

// The [MessageIdentity] attribute is only necessary
// because the projects aren't sharing types
// You would not do this if you were distributing
// message types through shared assemblies
[MessageIdentity("Ping")]
public class PingMessage
{
    public int Number { get; set; }
}

[MessageIdentity("Pong")]
public class PongMessage
{
    public int Number { get; set; }
}