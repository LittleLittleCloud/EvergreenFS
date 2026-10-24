using System.Collections;
using System.Text;

namespace EvergreenFS;

internal sealed class LineEnumerable : IEnumerable<string>, IEnumerator<string>
{
    private readonly IVirtualFileSystem provider;
    private readonly string path;
    private readonly Encoding encoding;
    private ManagedStreamReader? reader;
    private bool claimed;
    public LineEnumerable(string path, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        provider = VirtualFileSystem.Current;
        this.path = path;
        this.encoding = encoding;
        reader = new ManagedStreamReader(path, encoding);
    }
    public string Current { get; private set; } = null!;
    object IEnumerator.Current => Current;
    public IEnumerator<string> GetEnumerator()
    {
        if (!claimed) { claimed = true; return this; }
        using var scope = VirtualFileSystem.Use(provider);
        return new LineEnumerable(path, encoding) { claimed = true };
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool MoveNext()
    {
        if (reader == null) return false;
        var line = reader.ReadLine();
        if (line == null) { Dispose(); return false; }
        Current = line;
        return true;
    }
    public void Reset() => throw new NotSupportedException();
    public void Dispose() { reader?.Dispose(); reader = null; }
}
