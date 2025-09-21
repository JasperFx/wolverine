using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Persistence.Durability;

namespace Wolverine.Persistence;

public class ApplyAncillaryStoreFrame<T> : MethodCall
{
    public ApplyAncillaryStoreFrame() : base(typeof(AncillaryMessageStoreApplication<T> ), "Apply")
    {
    }

}