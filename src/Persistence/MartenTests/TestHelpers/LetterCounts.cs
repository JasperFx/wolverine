using JasperFx;

namespace MartenTests.TestHelpers;

public class LetterCounts: IRevisioned
{
    public Guid Id { get; set; }
    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }
    public int ECount { get; set; }
    public int Version { get; set; }
}