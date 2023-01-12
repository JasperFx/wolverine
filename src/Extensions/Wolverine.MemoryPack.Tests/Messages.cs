using MemoryPack;

namespace Wolverine.MemoryPack.Tests;

[MemoryPackable]
public partial class  MemoryPackMessage
{
    public string Name;
}

// fake handler
public class MemoryPackMessageHandler
{
    public void Handle(MemoryPackMessage message)
    {
    }
}