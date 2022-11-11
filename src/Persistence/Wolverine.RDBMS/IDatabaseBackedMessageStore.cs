using System.Data.Common;
using System.Threading.Tasks;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS;

public interface IDatabaseBackedMessageStore : IMessageStore
{
    public AdvancedSettings Settings { get; }

    public DatabaseSettings DatabaseSettings { get; }
    
    // TODO -- should there be a cancellation token here?
    Task StoreIncomingAsync(DbTransaction tx, Envelope[] envelopes);
    Task StoreOutgoingAsync(DbTransaction tx, Envelope[] envelopes);
}
