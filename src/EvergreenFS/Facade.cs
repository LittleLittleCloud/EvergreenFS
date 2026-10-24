using System.Text;

namespace EvergreenFS;

public static class VirtualFileSystem
{
    private static readonly AsyncLocal<IVirtualFileSystem?> current = new();
    public static IVirtualFileSystem Current => current.Value ??
        throw new InvalidOperationException("No EvergreenFS mount is active. Use VirtualFileSystem.Use(provider) before System.IO calls.");
    public static IDisposable Use(IVirtualFileSystem provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var previous = current.Value;
        current.Value = provider;
        return new Scope(() => current.Value = previous);
    }
    private sealed class Scope(Action restore) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { restore(); disposed = true; } }
    }
}

public static class FileFacade
{
    public static byte[] ReadAllBytes(string path)
    {
        var fs = VirtualFileSystem.Current;
        return ManagedFileStream.WithAccess(fs, path, FileAccess.Read, FileShare.Read, () => fs.ReadAllBytes(path));
    }
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        WriteAllBytes(path, bytes.AsSpan());
    }
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var copy = bytes.ToArray();
        var fs = VirtualFileSystem.Current;
        ManagedFileStream.WithAccess(fs, path, FileAccess.Write, FileShare.Read, () => { fs.WriteAllBytes(path, copy); return true; });
    }
    public static bool Exists(string? path) => VirtualFileSystem.Current.FileExists(path);
    public static void Delete(string path)
    {
        var fs = VirtualFileSystem.Current;
        ManagedFileStream.ExclusiveOperation(fs, () =>
        {
            ManagedFileStream.CheckDelete(fs, path);
            fs.DeleteFile(path);
        });
    }
    public static string ReadAllText(string path) => ReadAllText(path, Encoding.UTF8);
    public static string ReadAllText(string path, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        using var stream = new MemoryStream(ReadAllBytes(path));
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
    public static void WriteAllText(string path, string? contents) => WriteAllText(path, contents, new UTF8Encoding(false, true));
    public static void WriteAllText(string path, string? contents, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(contents ?? "")]);
    }
    public static void WriteAllText(string path, ReadOnlySpan<char> contents) => WriteAllText(path, contents.ToString());
    public static void WriteAllText(string path, ReadOnlySpan<char> contents, Encoding encoding) => WriteAllText(path, contents.ToString(), encoding);
    public static ManagedFileStream Open(string path, FileMode mode) => new(path, mode);
    public static ManagedFileStream Open(string path, FileMode mode, FileAccess access) => new(path, mode, access);
    public static ManagedFileStream Open(string path, FileMode mode, FileAccess access, FileShare share) => new(path, mode, access, share);
    public static ManagedFileStream Open(string path, FileStreamOptions options) => new(path, options);
    public static ManagedFileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    public static ManagedFileStream OpenWrite(string path) => new(path, FileMode.OpenOrCreate, FileAccess.Write);
    public static ManagedFileStream Create(string path) => new(path, FileMode.Create);
    public static ManagedFileStream Create(string path, int bufferSize) => new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize);
    public static ManagedFileStream Create(string path, int bufferSize, FileOptions options) => new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize, options);
    public static ManagedStreamReader OpenText(string path) => new(path);
    public static ManagedStreamWriter CreateText(string path) => new(path);
    public static ManagedStreamWriter AppendText(string path) => new(path, true);
    public static void AppendAllBytes(string path, byte[] bytes) { ArgumentNullException.ThrowIfNull(bytes); AppendAllBytes(path, bytes.AsSpan()); }
    public static void AppendAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var copy = bytes.ToArray();
        var fs = VirtualFileSystem.Current;
        ManagedFileStream.WithAccess(fs, path, FileAccess.Write, FileShare.Read, () =>
        {
            var current = fs.FileExists(path) ? fs.ReadAllBytes(path) : [];
            fs.WriteAllBytes(path, [.. current, .. copy]);
            return true;
        });
    }
    public static void AppendAllText(string path, string? contents) => AppendAllText(path, contents, new UTF8Encoding(false, true));
    public static void AppendAllText(string path, string? contents, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        var bytes = encoding.GetBytes(contents ?? "");
        var fs = VirtualFileSystem.Current;
        ManagedFileStream.WithAccess(fs, path, FileAccess.Write, FileShare.Read, () =>
        {
            var current = fs.FileExists(path) ? fs.ReadAllBytes(path) : [];
            fs.WriteAllBytes(path, current.Length == 0
                ? [.. encoding.GetPreamble(), .. bytes] : [.. current, .. bytes]);
            return true;
        });
    }
    public static void AppendAllText(string path, ReadOnlySpan<char> contents) => AppendAllText(path, contents.ToString());
    public static void AppendAllText(string path, ReadOnlySpan<char> contents, Encoding encoding) => AppendAllText(path, contents.ToString(), encoding);
    public static string[] ReadAllLines(string path) => ReadAllLines(path, Encoding.UTF8);
    public static string[] ReadAllLines(string path, Encoding encoding)
    {
        using var reader = new ManagedStreamReader(path, encoding);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines.ToArray();
    }
    public static IEnumerable<string> ReadLines(string path) => ReadLines(path, Encoding.UTF8);
    public static IEnumerable<string> ReadLines(string path, Encoding encoding) => new LineEnumerable(path, encoding);
    public static void WriteAllLines(string path, string[] contents) => WriteAllLines(path, (IEnumerable<string>)contents);
    public static void WriteAllLines(string path, string[] contents, Encoding encoding) => WriteAllLines(path, (IEnumerable<string>)contents, encoding);
    public static void WriteAllLines(string path, IEnumerable<string> contents) => WriteAllLines(path, contents, new UTF8Encoding(false, true));
    public static void WriteAllLines(string path, IEnumerable<string> contents, Encoding encoding) => Lines(path, contents, encoding, false);
    public static void AppendAllLines(string path, IEnumerable<string> contents) => AppendAllLines(path, contents, new UTF8Encoding(false, true));
    public static void AppendAllLines(string path, IEnumerable<string> contents, Encoding encoding) => Lines(path, contents, encoding, true);
    private static void Lines(string path, IEnumerable<string> contents, Encoding encoding, bool append)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(encoding);
        // Materialize before opening: a throwing enumerable must not truncate a file.
        var text = string.Concat(contents.Select(line => line + Environment.NewLine));
        if (append) AppendAllText(path, text, encoding); else WriteAllText(path, text, encoding);
    }
    public static void Copy(string sourceFileName, string destFileName) => Copy(sourceFileName, destFileName, false);
    public static void Copy(string sourceFileName, string destFileName, bool overwrite)
    {
        var fs = VirtualFileSystem.Current;
        ManagedFileStream.ExclusiveOperation(fs, () =>
        {
            ManagedFileStream.CheckDelete(fs, destFileName);
            using var source = OpenRead(sourceFileName);
            fs.CopyFile(sourceFileName, destFileName, overwrite);
        });
    }
    public static void Move(string sourceFileName, string destFileName) => Move(sourceFileName, destFileName, false);
    public static void Move(string sourceFileName, string destFileName, bool overwrite)
    {
        var fs = VirtualFileSystem.Current;
        ManagedFileStream.ExclusiveOperation(fs, () =>
        {
            ManagedFileStream.CheckDelete(fs, sourceFileName);
            ManagedFileStream.CheckDelete(fs, destFileName);
            fs.MoveFile(sourceFileName, destFileName, overwrite);
        });
    }
    public static void Replace(string sourceFileName, string destinationFileName, string? destinationBackupFileName) =>
        Replace(sourceFileName, destinationFileName, destinationBackupFileName, false);
    public static void Replace(string sourceFileName, string destinationFileName, string? destinationBackupFileName, bool ignoreMetadataErrors)
    {
        var fs = VirtualFileSystem.Current;
        ManagedFileStream.ExclusiveOperation(fs, () =>
        {
            ManagedFileStream.CheckDelete(fs, sourceFileName);
            ManagedFileStream.CheckDelete(fs, destinationFileName);
            if (destinationBackupFileName != null) ManagedFileStream.CheckDelete(fs, destinationBackupFileName);
            fs.ReplaceFile(sourceFileName, destinationFileName, destinationBackupFileName);
        });
    }
    public static FileAttributes GetAttributes(string path) => VirtualFileSystem.Current.GetAttributes(path);
    public static void SetAttributes(string path, FileAttributes fileAttributes) => VirtualFileSystem.Current.SetAttributes(path, fileAttributes);
    private static Task<T> Async<T>(Func<T> action, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled<T>(token);
        try { return Task.FromResult(action()); } catch (Exception error) { return Task.FromException<T>(error); }
    }
    private static Task Async(Action action, CancellationToken token) => Async(() => { action(); return true; }, token);
    public static Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default) => Async(() => ReadAllBytes(path), cancellationToken);
    public static Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default) => Async(() => WriteAllBytes(path, bytes), cancellationToken);
    public static Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => Async(() => WriteAllBytes(path, bytes.Span), cancellationToken);
    public static Task AppendAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default) => Async(() => AppendAllBytes(path, bytes), cancellationToken);
    public static Task AppendAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => Async(() => AppendAllBytes(path, bytes.Span), cancellationToken);
    public static Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) => ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
    public static Task<string> ReadAllTextAsync(string path, Encoding encoding, CancellationToken cancellationToken = default) => Async(() => ReadAllText(path, encoding), cancellationToken);
    public static Task WriteAllTextAsync(string path, string? contents, CancellationToken cancellationToken = default) => WriteAllTextAsync(path, contents, new UTF8Encoding(false, true), cancellationToken);
    public static Task WriteAllTextAsync(string path, string? contents, Encoding encoding, CancellationToken cancellationToken = default) => Async(() => WriteAllText(path, contents, encoding), cancellationToken);
    public static Task WriteAllTextAsync(string path, ReadOnlyMemory<char> contents, CancellationToken cancellationToken = default) => WriteAllTextAsync(path, contents.ToString(), cancellationToken);
    public static Task WriteAllTextAsync(string path, ReadOnlyMemory<char> contents, Encoding encoding, CancellationToken cancellationToken = default) => WriteAllTextAsync(path, contents.ToString(), encoding, cancellationToken);
    public static Task AppendAllTextAsync(string path, string? contents, CancellationToken cancellationToken = default) => AppendAllTextAsync(path, contents, new UTF8Encoding(false, true), cancellationToken);
    public static Task AppendAllTextAsync(string path, string? contents, Encoding encoding, CancellationToken cancellationToken = default) => Async(() => AppendAllText(path, contents, encoding), cancellationToken);
    public static Task AppendAllTextAsync(string path, ReadOnlyMemory<char> contents, CancellationToken cancellationToken = default) => AppendAllTextAsync(path, contents.ToString(), cancellationToken);
    public static Task AppendAllTextAsync(string path, ReadOnlyMemory<char> contents, Encoding encoding, CancellationToken cancellationToken = default) => AppendAllTextAsync(path, contents.ToString(), encoding, cancellationToken);
    public static Task<string[]> ReadAllLinesAsync(string path, CancellationToken cancellationToken = default) => Async(() => ReadAllLines(path), cancellationToken);
    public static Task<string[]> ReadAllLinesAsync(string path, Encoding encoding, CancellationToken cancellationToken = default) => Async(() => ReadAllLines(path, encoding), cancellationToken);
    public static Task WriteAllLinesAsync(string path, IEnumerable<string> contents, CancellationToken cancellationToken = default) => Async(() => WriteAllLines(path, contents), cancellationToken);
    public static Task WriteAllLinesAsync(string path, IEnumerable<string> contents, Encoding encoding, CancellationToken cancellationToken = default) => Async(() => WriteAllLines(path, contents, encoding), cancellationToken);
    public static Task AppendAllLinesAsync(string path, IEnumerable<string> contents, CancellationToken cancellationToken = default) => Async(() => AppendAllLines(path, contents), cancellationToken);
    public static Task AppendAllLinesAsync(string path, IEnumerable<string> contents, Encoding encoding, CancellationToken cancellationToken = default) => Async(() => AppendAllLines(path, contents, encoding), cancellationToken);
}

public static class DirectoryFacade
{
    public static ManagedDirectoryInfo CreateDirectory(string path)
    {
        VirtualFileSystem.Current.CreateDirectory(path);
        return new ManagedDirectoryInfo(path);
    }
    public static bool Exists(string? path) => VirtualFileSystem.Current.DirectoryExists(path);
    public static void Delete(string path) => Delete(path, false);
    public static void Delete(string path, bool recursive)
    {
        var fs = VirtualFileSystem.Current;
        ManagedFileStream.ExclusiveOperation(fs, () =>
        {
            ManagedFileStream.CheckTreeDelete(fs, path);
            fs.DeleteDirectory(path, recursive);
        });
    }
    public static string[] GetFiles(string path) => VirtualFileSystem.Current.GetFiles(path);
    public static string[] GetDirectories(string path) => VirtualFileSystem.Current.GetDirectories(path);
    public static IEnumerable<string> EnumerateFiles(string path) => GetFiles(path);
    public static IEnumerable<string> EnumerateDirectories(string path) => GetDirectories(path);
    public static string[] GetFiles(string path, string searchPattern) => GetFiles(path, searchPattern, SearchOption.TopDirectoryOnly);
    public static string[] GetFiles(string path, string searchPattern, SearchOption searchOption) => Enumerate(path, searchPattern, searchOption, false).ToArray();
    public static string[] GetFiles(string path, string searchPattern, EnumerationOptions enumerationOptions) => Enumerate(path, searchPattern, enumerationOptions, false).ToArray();
    public static string[] GetDirectories(string path, string searchPattern) => GetDirectories(path, searchPattern, SearchOption.TopDirectoryOnly);
    public static string[] GetDirectories(string path, string searchPattern, SearchOption searchOption) => Enumerate(path, searchPattern, searchOption, true).ToArray();
    public static string[] GetDirectories(string path, string searchPattern, EnumerationOptions enumerationOptions) => Enumerate(path, searchPattern, enumerationOptions, true).ToArray();
    public static IEnumerable<string> EnumerateFiles(string path, string searchPattern) => GetFiles(path, searchPattern);
    public static IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) => GetFiles(path, searchPattern, searchOption);
    public static IEnumerable<string> EnumerateFiles(string path, string searchPattern, EnumerationOptions enumerationOptions) => GetFiles(path, searchPattern, enumerationOptions);
    public static IEnumerable<string> EnumerateDirectories(string path, string searchPattern) => GetDirectories(path, searchPattern);
    public static IEnumerable<string> EnumerateDirectories(string path, string searchPattern, SearchOption searchOption) => GetDirectories(path, searchPattern, searchOption);
    public static IEnumerable<string> EnumerateDirectories(string path, string searchPattern, EnumerationOptions enumerationOptions) => GetDirectories(path, searchPattern, enumerationOptions);
    public static string[] GetFileSystemEntries(string path) => GetFileSystemEntries(path, "*");
    public static string[] GetFileSystemEntries(string path, string searchPattern) => GetFileSystemEntries(path, searchPattern, SearchOption.TopDirectoryOnly);
    public static string[] GetFileSystemEntries(string path, string searchPattern, SearchOption searchOption) =>
        GetFiles(path, searchPattern, searchOption).Concat(GetDirectories(path, searchPattern, searchOption)).Order(StringComparer.Ordinal).ToArray();
    public static string[] GetFileSystemEntries(string path, string searchPattern, EnumerationOptions enumerationOptions) =>
        GetFiles(path, searchPattern, enumerationOptions).Concat(GetDirectories(path, searchPattern, enumerationOptions)).Order(StringComparer.Ordinal).ToArray();
    public static IEnumerable<string> EnumerateFileSystemEntries(string path) => GetFileSystemEntries(path);
    public static IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern) => GetFileSystemEntries(path, searchPattern);
    public static IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern, SearchOption searchOption) => GetFileSystemEntries(path, searchPattern, searchOption);
    public static IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern, EnumerationOptions enumerationOptions) => GetFileSystemEntries(path, searchPattern, enumerationOptions);
    private static IEnumerable<string> Enumerate(string path, string pattern, SearchOption option, bool directories)
    {
        if (option != SearchOption.TopDirectoryOnly && option != SearchOption.AllDirectories) throw new ArgumentOutOfRangeException(nameof(option));
        return Enumerate(path, pattern, new EnumerationOptions { RecurseSubdirectories = option == SearchOption.AllDirectories, AttributesToSkip = 0, MatchType = MatchType.Win32 }, directories);
    }
    private static IEnumerable<string> Enumerate(string path, string pattern, EnumerationOptions options, bool directories)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(options);
        if (pattern.Contains('/') || pattern.Contains('\\') || pattern == "..") throw new ArgumentException("Search pattern must be a filename pattern.");
        if (options.ReturnSpecialDirectories) throw new NotSupportedException("Special dot directories are not supported.");
        if (!Enum.IsDefined(options.MatchCasing) || !Enum.IsDefined(options.MatchType)) throw new ArgumentOutOfRangeException(nameof(options));
        var fs = VirtualFileSystem.Current;
        var result = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((path, 0));
        var ignoreCase = options.MatchCasing == MatchCasing.CaseInsensitive;
        while (pending.TryPop(out var current))
        {
            var children = fs.GetDirectories(current.Path);
            foreach (var item in directories ? children : fs.GetFiles(current.Path))
            {
                if ((fs.GetAttributes(item) & options.AttributesToSkip) != 0) continue;
                var name = Path.GetFileName(item);
                var matches = options.MatchType == MatchType.Win32
                    ? System.IO.Enumeration.FileSystemName.MatchesWin32Expression(System.IO.Enumeration.FileSystemName.TranslateWin32Expression(pattern), name, ignoreCase)
                    : System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase);
                if (matches) result.Add(item);
            }
            if (options.RecurseSubdirectories && current.Depth < options.MaxRecursionDepth)
                foreach (var child in children)
                    if ((fs.GetAttributes(child) & options.AttributesToSkip) == 0) pending.Push((child, current.Depth + 1));
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }
    public static void Move(string sourceDirName, string destDirName)
    {
        var fs = VirtualFileSystem.Current;
        ManagedFileStream.ExclusiveOperation(fs, () =>
        {
            ManagedFileStream.CheckTreeDelete(fs, sourceDirName);
            ManagedFileStream.CheckTreeDelete(fs, destDirName);
            fs.MoveDirectory(sourceDirName, destDirName);
        });
    }
    public static ManagedDirectoryInfo? GetParent(string path)
    {
        var fs = VirtualFileSystem.Current;
        var key = VirtualPath.Resolve(fs.Mount, path);
        return key == "/" ? null : new ManagedDirectoryInfo(VirtualPath.Display(fs.Mount, VirtualPath.Parent(key)));
    }
    public static string GetDirectoryRoot(string path)
    {
        VirtualPath.Resolve(VirtualFileSystem.Current.Mount, path);
        return VirtualFileSystem.Current.Mount;
    }
}
