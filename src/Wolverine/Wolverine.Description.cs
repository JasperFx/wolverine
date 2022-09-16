using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Oakton.Descriptions;

namespace Wolverine;

public partial class WolverineOptions : IDescribedSystemPart, IWriteToConsole
{
    async Task IDescribedSystemPart.Write(TextWriter writer)
    {
        foreach (var transport in _transports.Values.Where(x => x.Endpoints().Any()))
        {
            await writer.WriteLineAsync(transport.Name);

            foreach (var endpoint in transport.Endpoints())
            {
                await writer.WriteLineAsync(
                    $"{endpoint.Uri}, Incoming: {endpoint.IsListener}, Reply Uri: {endpoint.IsUsedForReplies}");
            }

            await writer.WriteLineAsync();
        }
    }

    string IDescribedSystemPart.Title => "Wolverine Messaging Endpoints";

}
