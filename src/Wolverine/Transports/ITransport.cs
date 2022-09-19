using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Transports;


public interface ITransport
{
    ICollection<string> Protocols { get; }


    /// <summary>
    ///     Strictly a diagnostic name for this transport type
    /// </summary>
    string Name { get; }

    Endpoint? ReplyEndpoint();

    Endpoint ListenTo(Uri uri);

    void StartSenders(IWolverineRuntime root);

    Endpoint GetOrCreateEndpoint(Uri uri);
    Endpoint? TryGetEndpoint(Uri uri);

    [Obsolete]
    IEnumerable<Endpoint> Endpoints();
    
    ValueTask InitializeAsync(IWolverineRuntime runtime);
}
