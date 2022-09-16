using System;
using Wolverine.Attributes;

namespace Wolverine.Persistence.Testing.Marten;

[MessageIdentity("Message1")]
public class Message1
{
    public Guid Id = Guid.NewGuid();
}

[MessageIdentity("Message2")]
public class Message2
{
    public Guid Id = Guid.NewGuid();
}

[MessageIdentity("Message3")]
public class Message3
{
}

[MessageIdentity("Message4")]
public class Message4
{
}

[MessageIdentity("Message5")]
public class Message5
{
    public int FailThisManyTimes = 0;
    public Guid Id = Guid.NewGuid();
}
