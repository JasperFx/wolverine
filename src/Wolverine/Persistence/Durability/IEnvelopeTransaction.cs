using System.Threading.Tasks;

namespace Wolverine.Persistence.Durability;


// TODO -- should these all be ValueTask?
public interface IEnvelopeTransaction
{
    Task PersistAsync(Envelope envelope);
    Task PersistAsync(Envelope[] envelopes);
    Task ScheduleJobAsync(Envelope envelope);

    Task CopyToAsync(IEnvelopeTransaction other);

    ValueTask RollbackAsync();
}
