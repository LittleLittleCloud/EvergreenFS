using System.Runtime.CompilerServices;

namespace EvergreenFS;

public sealed class ManagedFileStream : Stream
{
    private sealed class OpenFile
    {
        public List<ManagedFileStream> Handles { get; } = [];
    }
    private sealed class OpenFiles
    {
        public object Gate { get; } = new();
        public Dictionary<string, OpenFile> Files { get; } = new(StringComparer.Ordinal);
    }
    private static readonly ConditionalWeakTable<IVirtualFileSystem, OpenFiles> tables = new();
    private readonly IVirtualFileSystem fs;
    private readonly OpenFiles table;
    private readonly OpenFile file;
    private readonly MemoryStream buffer = new();
    private bool dirty;
    private readonly FileAccess access;
    private readonly FileShare share;
    private readonly long appendFloor;
    private readonly bool unbuffered;
    private long position;
    private bool disposed;
    public string Name { get; }
    public bool IsAsync { get; }
    public ManagedFileStream(string path, FileMode mode) : this(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite) { }
    public ManagedFileStream(string path, FileMode mode, FileAccess access) : this(path, mode, access, FileShare.None) { }
    public ManagedFileStream(string path, FileMode mode, FileAccess access, FileShare share) : this(path, mode, access, share, 4096) { }
    public ManagedFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize)
        : this(path, mode, access, share, bufferSize, FileOptions.None) { }
    public ManagedFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, bool useAsync)
        : this(path, mode, access, share, bufferSize, useAsync ? FileOptions.Asynchronous : FileOptions.None) { }
    public ManagedFileStream(string path, FileStreamOptions options)
        : this(path, Options(options).Mode, options.Access, options.Share, options.BufferSize, ValidateOptions(options)) { }
    private static FileStreamOptions Options(FileStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options;
    }
    private static FileOptions ValidateOptions(FileStreamOptions options)
    {
        if (options.PreallocationSize != 0 || options.UnixCreateMode != null)
            throw new NotSupportedException("Preallocation and UnixCreateMode are not supported.");
        return options.Options;
    }
    public ManagedFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options)
    {
        if (mode < FileMode.CreateNew || mode > FileMode.Append) throw new ArgumentOutOfRangeException(nameof(mode));
        if (access < FileAccess.Read || access > FileAccess.ReadWrite) throw new ArgumentOutOfRangeException(nameof(access));
        if ((share & ~(FileShare.ReadWrite | FileShare.Delete | FileShare.Inheritable)) != 0) throw new ArgumentOutOfRangeException(nameof(share));
        if ((share & FileShare.Inheritable) != 0) throw new NotSupportedException("Native descriptor inheritance is unsupported.");
        if (bufferSize < 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        if ((options & ~(FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.RandomAccess | FileOptions.WriteThrough)) != 0)
            throw new NotSupportedException("Only Asynchronous, SequentialScan, RandomAccess and WriteThrough options are supported; no native handles or DeleteOnClose.");
        if ((mode is FileMode.Create or FileMode.CreateNew or FileMode.Truncate or FileMode.Append) && access == FileAccess.Read)
            throw new ArgumentException("This file mode requires write access.", nameof(access));
        if (mode == FileMode.Append && access != FileAccess.Write) throw new ArgumentException("Append requires write-only access.");
        fs = VirtualFileSystem.Current;
        Name = VirtualPath.Display(fs.Mount, VirtualPath.Resolve(fs.Mount, path));
        this.access = access;
        this.share = share & ~FileShare.Inheritable;
        IsAsync = (options & FileOptions.Asynchronous) != 0;
        unbuffered = bufferSize <= 1 || (options & FileOptions.WriteThrough) != 0;
        table = tables.GetOrCreateValue(fs);
        lock (table.Gate)
        {
            if (table.Files.TryGetValue(Name, out var existing))
                foreach (var handle in existing.Handles)
                    if (!Allows(handle.share, access) || !Allows(this.share, handle.access) ||
                        (handle.access & FileAccess.Write) != 0 && (access & FileAccess.Write) != 0)
                        throw new IOException("The virtual file is in use with incompatible sharing.");
            var exists = fs.FileExists(Name);
            if (mode == FileMode.CreateNew && exists) throw new IOException("File already exists.");
            if (mode is FileMode.Open or FileMode.Truncate && !exists) fs.ReadAllBytes(Name);
            if ((access & FileAccess.Write) != 0 && exists && (fs.GetAttributes(Name) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("File is read-only.");
            if (!exists || mode is FileMode.Create or FileMode.Truncate)
                fs.WriteAllBytes(Name, []);
            file = existing ?? new OpenFile();
            buffer.Write(fs.ReadAllBytes(Name));
            if (existing == null) table.Files.Add(Name, file);
            appendFloor = mode == FileMode.Append ? buffer.Length : 0;
            position = appendFloor;
            file.Handles.Add(this);
        }
    }
    private static bool Allows(FileShare share, FileAccess access) =>
        ((access & FileAccess.Read) == 0 || (share & FileShare.Read) != 0) &&
        ((access & FileAccess.Write) == 0 || (share & FileShare.Write) != 0);
    internal static void CheckDelete(IVirtualFileSystem fs, string path)
    {
        var name = VirtualPath.Display(fs.Mount, VirtualPath.Resolve(fs.Mount, path));
        var table = tables.GetOrCreateValue(fs);
        lock (table.Gate)
            if (table.Files.TryGetValue(name, out var file) && file.Handles.Count != 0)
                throw new IOException("Deleting or renaming open virtual files is unsupported, even with FileShare.Delete.");
    }
    internal static void CheckTreeDelete(IVirtualFileSystem fs, string path)
    {
        var prefix = VirtualPath.Display(fs.Mount, VirtualPath.Resolve(fs.Mount, path));
        var table = tables.GetOrCreateValue(fs);
        lock (table.Gate)
            if (table.Files.Keys.Any(name => name == prefix || name.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.Ordinal)))
                throw new IOException("Directory contains open virtual handles.");
    }
    internal static void ExclusiveOperation(IVirtualFileSystem fs, Action action)
    {
        lock (tables.GetOrCreateValue(fs).Gate) action();
    }
    internal static T WithAccess<T>(IVirtualFileSystem fs, string path, FileAccess access, FileShare share, Func<T> action)
    {
        var name = VirtualPath.Display(fs.Mount, VirtualPath.Resolve(fs.Mount, path));
        var table = tables.GetOrCreateValue(fs);
        lock (table.Gate)
        {
            if (table.Files.TryGetValue(name, out var existing))
                foreach (var handle in existing.Handles)
                    if (!Allows(handle.share, access) || !Allows(share, handle.access))
                        throw new IOException("The virtual file is in use with incompatible sharing.");
            return action();
        }
    }
    private void Check(bool write = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (write ? !CanWrite : !CanRead) throw new NotSupportedException(write ? "Stream is not writable." : "Stream is not readable.");
    }
    public override bool CanRead => !disposed && (access & FileAccess.Read) != 0;
    public override bool CanWrite => !disposed && (access & FileAccess.Write) != 0;
    public override bool CanSeek => !disposed;
    private void RefreshCommitted()
    {
        if (dirty || (access & FileAccess.Write) != 0) return;
        var bytes = fs.ReadAllBytes(Name);
        buffer.SetLength(0);
        buffer.Write(bytes);
    }
    public override long Length { get { lock (table.Gate) { ObjectDisposedException.ThrowIf(disposed, this); RefreshCommitted(); return buffer.Length; } } }
    public override long Position
    {
        get { ObjectDisposedException.ThrowIf(disposed, this); return position; }
        set
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (value < appendFloor) throw new IOException("Cannot seek before the append origin.");
            position = value;
        }
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }
    public override int Read(Span<byte> buffer)
    {
        lock (table.Gate)
        {
            Check();
            RefreshCommitted();
            this.buffer.Position = position;
            var count = this.buffer.Read(buffer);
            position += count;
            return count;
        }
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (table.Gate)
        {
            Check(write: true);
            this.buffer.Position = position;
            this.buffer.Write(buffer);
            position += buffer.Length;
            dirty |= buffer.Length != 0;
            if (unbuffered) Flush();
        }
    }
    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count) throw new ArgumentException("Offset and count exceed the buffer.");
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentException("Invalid seek origin.", nameof(origin))
        };
        if (target < 0) throw new IOException("Cannot seek before the beginning of the file.");
        Position = target;
        return position;
    }
    public override void SetLength(long value)
    {
        lock (table.Gate)
        {
            Check(write: true);
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (value < appendFloor) throw new IOException("Cannot truncate before the append origin.");
            buffer.SetLength(value);
            if (position > value) position = value;
            dirty = true;
            if (unbuffered) Flush();
        }
    }
    public override void Flush()
    {
        lock (table.Gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!dirty) return;
            fs.WriteAllBytes(Name, buffer.ToArray());
            dirty = false;
        }
    }
    public void Flush(bool flushToDisk) => Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Run(() => Flush());
    private static Task Run(Action action)
    {
        try { action(); return Task.CompletedTask; }
        catch (Exception error) { return Task.FromException(error); }
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<int>(cancellationToken);
        try { return ValueTask.FromResult(Read(buffer.Span)); }
        catch (Exception error) { return ValueTask.FromException<int>(error); }
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBuffer(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled(cancellationToken);
        try { Write(buffer.Span); return ValueTask.CompletedTask; }
        catch (Exception error) { return ValueTask.FromException(error); }
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBuffer(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
    protected override void Dispose(bool disposing)
    {
        if (!disposing || disposed) return;
        lock (table.Gate)
        {
            try { Flush(); }
            finally
            {
                disposed = true;
                file.Handles.Remove(this);
                if (file.Handles.Count == 0)
                {
                    table.Files.Remove(Name);
                }
                buffer.Dispose();
            }
        }
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
