using System.Threading.Tasks;

namespace Wolverine.Persistence.Durability;


// TODO -- should these all be ValueTask?
public interface IEnvelopeOutbox
{
    Task PersistAsync(Envelope envelope);
    Task PersistAsync(Envelope[] envelopes);
    Task ScheduleJobAsync(Envelope envelope);

    Task CopyToAsync(IEnvelopeOutbox other);

    ValueTask RollbackAsync();
}
