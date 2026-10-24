using System.Text;

namespace EvergreenFS;

public class ManagedStreamReader : StreamReader
{
    public ManagedStreamReader(string path) : this(path, Encoding.UTF8, true) { }
    public ManagedStreamReader(string path, bool detectEncodingFromByteOrderMarks) : this(path, Encoding.UTF8, detectEncodingFromByteOrderMarks) { }
    public ManagedStreamReader(string path, Encoding encoding) : this(path, encoding, true) { }
    public ManagedStreamReader(string path, Encoding encoding, bool detectEncodingFromByteOrderMarks) : this(path, encoding, detectEncodingFromByteOrderMarks, 1024) { }
    public ManagedStreamReader(string path, Encoding encoding, bool detectEncodingFromByteOrderMarks, int bufferSize)
        : base(Open(path, encoding, bufferSize), encoding, detectEncodingFromByteOrderMarks, bufferSize) { }
    private static Stream Open(string path, Encoding encoding, int size)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        return FileFacade.OpenRead(path);
    }
    public ManagedStreamReader(Stream stream) : base(stream) { }
    public ManagedStreamReader(Stream stream, bool detectEncodingFromByteOrderMarks) : base(stream, detectEncodingFromByteOrderMarks) { }
    public ManagedStreamReader(Stream stream, Encoding? encoding) : base(stream, encoding) { }
    public ManagedStreamReader(Stream stream, Encoding? encoding, bool detectEncodingFromByteOrderMarks) : base(stream, encoding, detectEncodingFromByteOrderMarks) { }
    public ManagedStreamReader(Stream stream, Encoding? encoding, bool detectEncodingFromByteOrderMarks, int bufferSize)
        : base(stream, encoding, detectEncodingFromByteOrderMarks, bufferSize) { }
    public ManagedStreamReader(Stream stream, Encoding? encoding, bool detectEncodingFromByteOrderMarks, int bufferSize, bool leaveOpen)
        : base(stream, encoding, detectEncodingFromByteOrderMarks, bufferSize, leaveOpen) { }
}

public class ManagedStreamWriter : StreamWriter
{
    public ManagedStreamWriter(string path) : this(path, false) { }
    public ManagedStreamWriter(string path, bool append) : this(path, append, new UTF8Encoding(false, true)) { }
    public ManagedStreamWriter(string path, bool append, Encoding encoding) : this(path, append, encoding, 1024) { }
    public ManagedStreamWriter(string path, bool append, Encoding encoding, int bufferSize)
        : base(Open(path, append, encoding, bufferSize), encoding, bufferSize) { }
    private static Stream Open(string path, bool append, Encoding encoding, int size)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        return new ManagedFileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
    }
    public ManagedStreamWriter(Stream stream) : base(stream) { }
    public ManagedStreamWriter(Stream stream, Encoding? encoding) : base(stream, encoding) { }
    public ManagedStreamWriter(Stream stream, Encoding? encoding, int bufferSize) : base(stream, encoding, bufferSize) { }
    public ManagedStreamWriter(Stream stream, Encoding? encoding, int bufferSize, bool leaveOpen) : base(stream, encoding, bufferSize, leaveOpen) { }
}
