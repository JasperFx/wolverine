using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.RDBMS.Transport;

public class ExternalDbTransport : TransportBase<ExternalMessageTable>
{
    public static readonly string ProtocolName = "external-table";

    public LightweightCache<DbObjectName, ExternalMessageTable> Tables { get; }

    public ExternalDbTransport() : base(ProtocolName, "External Database Tables", ["external-db"])
    {
        Tables = new LightweightCache<DbObjectName, ExternalMessageTable>(name =>
            new ExternalMessageTable(name));
    }

    protected override IEnumerable<ExternalMessageTable> endpoints()
    {
        return Tables;
    }

    public override Endpoint? ReplyEndpoint()
    {
        return null;
    }

    protected override ExternalMessageTable findEndpointByUri(Uri uri)
    {
        if (uri.Scheme != Protocol)
        {
            throw new ArgumentOutOfRangeException(nameof(uri));
        }
        
        var existing =  Tables.Where(x => x.Uri.OriginalString == uri.OriginalString).FirstOrDefault();
        if (existing != null) return existing;

        var tableName = uri.OriginalString.Split("//")[1].TrimEnd('/');
        var parts = tableName.Split(".");
        var dbObjectName = new DbObjectName(parts[0], parts[1]);
        return Tables[dbObjectName];
    }
}