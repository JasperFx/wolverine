using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

public record Nothing<T> : IStorageAction<T>
{
    public static Frame BuildFrame(IChain chain, Variable variable, GenerationRules rules, IServiceContainer container)
    {
        return new CommentFrame("Do nothing with the entity");
    }

    public StorageAction Action => StorageAction.Nothing;
    public T Entity => default!;
}