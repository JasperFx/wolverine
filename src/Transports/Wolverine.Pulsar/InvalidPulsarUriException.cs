using System;

namespace Wolverine.Pulsar;

public class InvalidPulsarUriException : Exception
{
    public InvalidPulsarUriException(Uri actualUri) : base(
        $"Invalid Wolverine Pulsar Uri '{actualUri}'. Should be of form 'pulsar://persistent/non-persistent/tenant/namespace/topic'")
    {
    }
}