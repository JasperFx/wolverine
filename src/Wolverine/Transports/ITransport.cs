using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Oakton.Resources;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Transports;


public interface ITransport
{
    string Protocol { get; }

    /// <summary>
    ///     Strictly a diagnostic name for this transport type
    /// </summary>
    string Name { get; }

    Endpoint? ReplyEndpoint();

    Endpoint GetOrCreateEndpoint(Uri uri);
    Endpoint? TryGetEndpoint(Uri uri);

    IEnumerable<Endpoint> Endpoints();
    
    ValueTask InitializeAsync(IWolverineRuntime runtime);

    bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource);
}
