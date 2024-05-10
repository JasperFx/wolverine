namespace Wolverine.Tracking;

internal class Grid<T>
{
    private readonly List<Column<T>> _columns = new();

    public void AddColumn(string header, Func<T, string> source, bool rightJustified = false)
    {
        var column = new Column<T>(header, source)
        {
            RightJustified = rightJustified
        };

        _columns.Add(column);
    }

    public string Write(IReadOnlyList<T> items)
    {
        var writer = new StringWriter();

        var totalWidth = determineWidths(items);
        writer.WriteLine();
        writeSolidLine(writer, totalWidth);

        writeHeaderRow(writer);

        writeSolidLine(writer, totalWidth);

        foreach (var item in items)
        {
            writeBodyRow(writer, item);
        }

        writeSolidLine(writer, totalWidth);

        return writer.ToString();
    }

    private int determineWidths(IReadOnlyList<T> items)
    {
        foreach (var column in _columns)
        {
            column.DetermineWidth(items);
        }

        var totalWidth = _columns.Sum(x => x.Width) + (_columns.Count) + 1;
        return totalWidth;
    }

    private void writeBodyRow(StringWriter writer, T item)
    {
        foreach (var column in _columns)
        {
            column.WriteLine(writer, item);
        }

        writer.WriteLine('|');
    }

    private void writeHeaderRow(StringWriter writer)
    {
        foreach (var column in _columns)
        {
            column.WriteHeader(writer);
        }

        writer.WriteLine('|');
    }

    private static void writeSolidLine(StringWriter writer, int totalWidth)
    {
        writer.WriteLine("".PadRight(totalWidth, '-'));
    }
}

internal class Column<T>
{
    private readonly Func<T, string> _source;
    private int _width;
    public string Header { get; }

    public Column(string header, Func<T, string> source)
    {
        _source = source;
        Header = header;
    }

    public bool RightJustified { get; set; }

    public void DetermineWidth(IEnumerable<T> items)
    {
        _width = items.Select(x => _source(x)).Where(x => x != null).Max(x => x.Length);
        if (Header.Length > _width) _width = Header.Length;
        _width += 4;
    }

    public int Width => _width;

    public void WriteHeader(TextWriter writer)
    {
        writer.Write("| ");
        writer.Write(Header);
        writer.Write("".PadRight(_width - Header.Length - 1));
    }

    public void WriteLine(TextWriter writer, T item)
    {
        var value = _source(item) ?? string.Empty;
        writer.Write("| ");
        if (RightJustified)
        {
            writer.Write("".PadRight(_width - value.Length - 1));
            writer.Write(value);
        }
        else
        {
            writer.Write(value);
            writer.Write("".PadRight(_width - value.Length - 1));
        }
    }
}