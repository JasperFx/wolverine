using Microsoft.EntityFrameworkCore;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

public interface IDbContextBuilder<T> where T : DbContext
{
    ValueTask<T> BuildAndEnrollAsync(MessageContext messaging, CancellationToken cancellationToken);
    ValueTask<T> BuildAsync(CancellationToken cancellationToken);
    ValueTask<T> BuildAsync(string tenantId, CancellationToken cancellationToken);
}