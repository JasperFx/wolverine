using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Baseline;

namespace Wolverine.Runtime.Serialization;

internal class MimeTypeList : IEnumerable<string>
{
    private readonly IList<string> _mimeTypes = new List<string>();

    // put stuff after ';' over to the side
    // look for ',' separated values
    public MimeTypeList(string mimeType)
    {
        Raw = mimeType;


        IEnumerable<string> types =
            mimeType.ToDelimitedArray().Select(x => x.Split(';')[0]).Where(x => x.IsNotEmpty());

        _mimeTypes.AddRange(types);
    }

    public MimeTypeList(params string[] mimeTypes)
    {
        Raw = mimeTypes.Select(x => x).Join(";");
        _mimeTypes.AddRange(mimeTypes);
    }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public string Raw { get; }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public IEnumerator<string> GetEnumerator()
    {
        return _mimeTypes.GetEnumerator();
    }

    public void AddMimeType(string mimeType)
    {
        _mimeTypes.Add(mimeType);
    }

    public bool Matches(params string?[] mimeTypes)
    {
        return _mimeTypes.Intersect(mimeTypes).Any();
    }

    public override string ToString()
    {
        return _mimeTypes.Join(", ");
    }

    public bool AcceptsAny()
    {
        return _mimeTypes.Count == 0 || _mimeTypes.Contains("*/*");
    }
}
