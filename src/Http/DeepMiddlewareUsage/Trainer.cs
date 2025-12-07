using System.ComponentModel;
using Marten.Schema;

namespace DeepMiddlewareUsage;

public class Participant { }

public class Trainer: Participant
{
    [DefaultValue(null)] 
    [UniqueIndex(IndexType = UniqueIndexType.Computed)]
    public string? Name { get; set; }

    [DefaultValue(null)] 
    public string? Country { get; set; }

    [DefaultValue(null)] 
    public string? TimeZone { get; set; }
}