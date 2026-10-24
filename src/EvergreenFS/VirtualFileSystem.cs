using System.Text.Json;

namespace EvergreenFS;

public sealed record Revision(long Id, string Operation, string Path, DateTimeOffset Timestamp);

public interface IVirtualFileSystem : IDisposable
{
    string Mount { get; }
    byte[] ReadAllBytes(string path);
    void WriteAllBytes(string path, byte[] bytes);
    bool FileExists(string? path);
    bool DirectoryExists(string? path);
    void DeleteFile(string path);
    void CreateDirectory(string path);
    void DeleteDirectory(string path, bool recursive = false);
    string[] GetFiles(string path);
    string[] GetDirectories(string path);
    FileAttributes GetAttributes(string path);
    void SetAttributes(string path, FileAttributes attributes);
    void CopyFile(string source, string destination, bool overwrite = false);
    void MoveFile(string source, string destination, bool overwrite = false);
    void MoveDirectory(string source, string destination);
    void ReplaceFile(string source, string destination, string? backup);
    IReadOnlyList<Revision> History { get; }
    Snapshot Snapshot(long? revision = null);
    InMemoryFileSystem Fork(long revision);
}

public sealed class Snapshot
{
    private readonly Dictionary<string, byte[]?> entries;
    private readonly Dictionary<string, FileAttributes> attributes;
    public long Revision { get; }
    public string Mount { get; }
    internal Snapshot(long revision, string mount, Dictionary<string, byte[]?> entries, Dictionary<string, FileAttributes> attributes)
    {
        Revision = revision;
        Mount = mount;
        this.entries = VersionedFileSystem.Copy(entries);
        this.attributes = new(attributes, StringComparer.Ordinal);
    }
    public byte[] ReadAllBytes(string path) => VersionedFileSystem.Read(entries, VirtualPath.Resolve(Mount, path));
    public string[] Paths => entries.Keys.Select(p => VirtualPath.Display(Mount, p)).Order(StringComparer.Ordinal).ToArray();
    internal Dictionary<string, byte[]?> CopyEntries() => VersionedFileSystem.Copy(entries);
    internal Dictionary<string, FileAttributes> CopyAttributes() => new(attributes, StringComparer.Ordinal);
    public FileAttributes GetAttributes(string path) => attributes.TryGetValue(VirtualPath.Resolve(Mount, path), out var value)
        ? value : throw new FileNotFoundException("Snapshot path not found.", path);
}

internal static class VirtualPath
{
    public static string MountPath(string mount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mount);
        if (!mount.StartsWith('/') || mount.Contains('\\') || mount.Contains(':') ||
            mount.Split('/').Any(p => p is "." or ".."))
            throw new ArgumentException("Mount must be an absolute slash-separated virtual path.", nameof(mount));
        return mount.TrimEnd('/') is { Length: > 0 } value ? value : "/";
    }

    public static string Resolve(string mount, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.Contains('\0') || path.Contains('\\') || path.Contains(':'))
            throw new ArgumentException("Only slash-separated virtual paths without NUL, colon or backslash are supported.", nameof(path));
        if (path.StartsWith('/'))
        {
            if (mount != "/" && path != mount && !path.StartsWith(mount + "/", StringComparison.Ordinal))
                throw new UnauthorizedAccessException($"Path is outside virtual mount '{mount}'.");
            path = mount == "/" ? path[1..] : path[mount.Length..].TrimStart('/');
        }
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new UnauthorizedAccessException("Path escapes the virtual mount.");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }
    public static string Display(string mount, string key) => key == "/" ? mount : mount.TrimEnd('/') + key;
    public static string Parent(string key) => key[..key.LastIndexOf('/')] is { Length: > 0 } p ? p : "/";
}

public abstract class VersionedFileSystem : IVirtualFileSystem
{
    private readonly object gate = new();
    private readonly List<(Revision Revision, Dictionary<string, byte[]?> State)> revisions = [];
    private Dictionary<string, byte[]?> state = new(StringComparer.Ordinal) { ["/"] = null };
    private bool disposed;
    private Dictionary<string, FileAttributes> initialAttributes = new(StringComparer.Ordinal);
    public string Mount { get; }
    protected VersionedFileSystem(string mount) => Mount = VirtualPath.MountPath(mount);
    internal static Dictionary<string, byte[]?> Copy(Dictionary<string, byte[]?> source) =>
        source.ToDictionary(p => p.Key, p => p.Value?.ToArray(), StringComparer.Ordinal);
    protected void Initialize(Dictionary<string, byte[]?> initial, IEnumerable<(Revision, Dictionary<string, byte[]?>)>? history = null,
        Dictionary<string, FileAttributes>? attributes = null)
    {
        state = Copy(initial);
        if (history != null) revisions.AddRange(history);
        if (revisions.Count == 0) revisions.Add((new Revision(0, "initial", Mount, DateTimeOffset.UtcNow), Copy(state)));
        initialAttributes = attributes == null ? new(StringComparer.Ordinal) : new(attributes, StringComparer.Ordinal);
    }
    protected virtual void Persist(Revision revision, Dictionary<string, byte[]?> next) { }
    protected static byte[] Serialize(Dictionary<string, byte[]?> value) => JsonSerializer.SerializeToUtf8Bytes(value);
    protected static Dictionary<string, byte[]?> Deserialize(byte[] value) =>
        JsonSerializer.Deserialize<Dictionary<string, byte[]?>>(value) ?? throw new InvalidDataException("Invalid filesystem state.");
    private T Locked<T>(Func<T> action)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return action();
        }
    }
    public IReadOnlyList<Revision> History => Locked(() => (IReadOnlyList<Revision>)revisions.Select(r => r.Revision).ToArray());
    public Snapshot Snapshot(long? revision = null) => Locked(() =>
    {
        var item = revision == null ? revisions[^1] : revisions.FirstOrDefault(r => r.Revision.Id == revision);
        if (item.Revision == null) throw new ArgumentOutOfRangeException(nameof(revision), "Revision does not exist.");
        return new Snapshot(item.Revision.Id, Mount, item.State, AttributesAt(item.Revision.Id));
    });
    public InMemoryFileSystem Fork(long revision)
    {
        var snapshot = Snapshot(revision);
        return new(Mount, snapshot.CopyEntries(), snapshot.CopyAttributes());
    }
    internal static byte[] Read(Dictionary<string, byte[]?> entries, string key)
    {
        if (entries.TryGetValue(key, out var value))
            return value?.ToArray() ?? throw new UnauthorizedAccessException("Path is a directory.");
        RequireParent(entries, key);
        throw new FileNotFoundException("Virtual file not found.", key);
    }
    private static void RequireParent(Dictionary<string, byte[]?> entries, string key)
    {
        if (!entries.TryGetValue(VirtualPath.Parent(key), out var parent) || parent != null)
            throw new DirectoryNotFoundException("Parent directory does not exist.");
    }
    public byte[] ReadAllBytes(string path) => Locked(() => Read(state, VirtualPath.Resolve(Mount, path)));
    private bool Exists(string? path, bool directory) => Locked(() =>
    {
        if (string.IsNullOrEmpty(path)) return false;
        try { return state.TryGetValue(VirtualPath.Resolve(Mount, path), out var value) && (value == null) == directory; }
        catch (ArgumentException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    });
    public bool FileExists(string? path) => Exists(path, false);
    public bool DirectoryExists(string? path) => Exists(path, true);
    private void Mutate(string operation, string path, Func<Dictionary<string, byte[]?>, string, bool> change)
    {
        Locked(() =>
        {
            var key = VirtualPath.Resolve(Mount, path);
            var next = Copy(state);
            if (!change(next, key)) return false;
            var revision = new Revision(revisions[^1].Revision.Id + 1, operation, VirtualPath.Display(Mount, key), DateTimeOffset.UtcNow);
            Persist(revision, next);
            state = next;
            revisions.Add((revision, Copy(next)));
            return true;
        });
    }
    public void WriteAllBytes(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var copy = bytes.ToArray();
        Mutate("write", path, (next, key) =>
        {
            RequireWritable(path);
            RequireParent(next, key);
            if (next.TryGetValue(key, out var existing) && existing == null) throw new UnauthorizedAccessException("Path is a directory.");
            next[key] = copy;
            return true;
        });
    }
    public void DeleteFile(string path)
    {
        Mutate("delete-file", path, (next, key) =>
        {
            RequireWritable(path);
            RequireParent(next, key);
            if (next.TryGetValue(key, out var value) && value == null) throw new UnauthorizedAccessException("Path is a directory.");
            return next.Remove(key);
        });
    }
    public void CreateDirectory(string path) => Mutate("mkdir", path, (next, key) =>
    {
        var changed = false;
        var current = "";
        foreach (var part in key.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            if (next.TryGetValue(current, out var value))
            {
                if (value != null)
                {
                    if (current != key) throw new DirectoryNotFoundException("A parent component is a file.");
                    throw new IOException("A file occupies the directory path.");
                }
            }
            else { next[current] = null; changed = true; }
        }
        return changed;
    });
    public void DeleteDirectory(string path, bool recursive = false) => Mutate("delete-directory", path, (next, key) =>
    {
        if (key == "/") throw new IOException("Cannot delete the mount root.");
        if (!next.TryGetValue(key, out var value) || value != null) throw new DirectoryNotFoundException();
        var children = next.Keys.Where(p => p.StartsWith(key + "/", StringComparison.Ordinal)).ToArray();
        if (!recursive && children.Length != 0) throw new IOException("Directory is not empty.");
        foreach (var child in children) next.Remove(child);
        next.Remove(key);
        return true;
    });
    private string[] List(string path, bool directories) => Locked(() =>
    {
        var key = VirtualPath.Resolve(Mount, path);
        if (!state.TryGetValue(key, out var value) || value != null) throw new DirectoryNotFoundException();
        return state.Where(p => p.Key != "/" && VirtualPath.Parent(p.Key) == key && (p.Value == null) == directories)
            .Select(p => VirtualPath.Display(Mount, p.Key)).Order(StringComparer.Ordinal).ToArray();
    });
    public string[] GetFiles(string path) => List(path, false);
    public string[] GetDirectories(string path) => List(path, true);
    private Dictionary<string, FileAttributes> AttributesAt(long id)
    {
        var result = new Dictionary<string, FileAttributes>(initialAttributes, StringComparer.Ordinal);
        foreach (var (revision, entries) in revisions)
        {
            if (revision.Id > id) break;
            var previous = new Dictionary<string, FileAttributes>(result, StringComparer.Ordinal);
            foreach (var key in result.Keys.Where(k => !entries.ContainsKey(k)).ToArray()) result.Remove(key);
            foreach (var entry in entries) result.TryAdd(entry.Key, entry.Value == null ? FileAttributes.Directory : FileAttributes.Normal);
            var keyAtRevision = VirtualPath.Resolve(Mount, revision.Path);
            var operation = revision.Operation;
            if (operation.StartsWith("attributes:", StringComparison.Ordinal))
                result[keyAtRevision] = (FileAttributes)int.Parse(operation[11..], System.Globalization.CultureInfo.InvariantCulture);
            else if (operation.StartsWith("copy:", StringComparison.Ordinal) || operation.StartsWith("move:", StringComparison.Ordinal))
                result[keyAtRevision] = previous[operation[5..]];
            else if (operation.StartsWith("move-directory:", StringComparison.Ordinal))
            {
                var source = operation[15..];
                foreach (var entry in previous.Where(p => p.Key == source || p.Key.StartsWith(source + "/", StringComparison.Ordinal)))
                    result[keyAtRevision + entry.Key[source.Length..]] = entry.Value;
            }
            else if (operation.StartsWith("replace:", StringComparison.Ordinal))
            {
                var paths = JsonSerializer.Deserialize<string?[]>(operation[8..])!;
                result[keyAtRevision] = previous[paths[0]!];
                if (paths[1] != null) result[paths[1]!] = previous[keyAtRevision];
            }
        }
        return result;
    }
    public FileAttributes GetAttributes(string path) => Locked(() =>
    {
        var key = VirtualPath.Resolve(Mount, path);
        if (!state.ContainsKey(key)) Read(state, key);
        return AttributesAt(revisions[^1].Revision.Id)[key];
    });
    public void SetAttributes(string path, FileAttributes attributes)
    {
        if ((attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive | FileAttributes.Normal | FileAttributes.Directory)) != 0)
            throw new NotSupportedException("Only ReadOnly, Hidden, Archive, Normal and Directory attributes are supported.");
        var directory = DirectoryExists(path);
        var effective = directory ? attributes | FileAttributes.Directory : attributes & ~FileAttributes.Directory;
        if (effective == 0) effective = FileAttributes.Normal;
        if ((effective & ~FileAttributes.Normal) != 0) effective &= ~FileAttributes.Normal;
        Mutate("attributes:" + (int)effective, path, (next, key) =>
        {
            if (!next.ContainsKey(key)) Read(next, key);
            return true;
        });
    }
    private void RequireWritable(string path)
    {
        if (FileExists(path) && (GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("File is read-only.");
    }
    public void CopyFile(string source, string destination, bool overwrite = false)
    {
        var sourceKey = VirtualPath.Resolve(Mount, source);
        Mutate("copy:" + sourceKey, destination, (next, key) =>
        {
            RequireWritable(destination);
            var bytes = Read(next, sourceKey);
            RequireParent(next, key);
            if (sourceKey == key) throw new IOException("Source and destination are the same.");
            if (next.TryGetValue(key, out var value) && (value == null || !overwrite)) throw new IOException("Destination already exists.");
            next[key] = bytes;
            return true;
        });
    }
    public void MoveFile(string source, string destination, bool overwrite = false)
    {
        var sourceKey = VirtualPath.Resolve(Mount, source);
        Mutate("move:" + sourceKey, destination, (next, key) =>
        {
            RequireWritable(source);
            RequireWritable(destination);
            var bytes = Read(next, sourceKey);
            RequireParent(next, key);
            if (sourceKey == key) throw new IOException("Source and destination are the same.");
            if (next.TryGetValue(key, out var value) && (value == null || !overwrite)) throw new IOException("Destination already exists.");
            next.Remove(sourceKey);
            next[key] = bytes;
            return true;
        });
    }
    public void MoveDirectory(string source, string destination)
    {
        var sourceKey = VirtualPath.Resolve(Mount, source);
        Mutate("move-directory:" + sourceKey, destination, (next, key) =>
        {
            if (sourceKey == "/" || key.StartsWith(sourceKey + "/", StringComparison.Ordinal)) throw new IOException("Invalid directory move.");
            if (!next.TryGetValue(sourceKey, out var value) || value != null) throw new DirectoryNotFoundException();
            RequireParent(next, key);
            if (next.ContainsKey(key)) throw new IOException("Destination exists.");
            var moved = next.Where(p => p.Key == sourceKey || p.Key.StartsWith(sourceKey + "/", StringComparison.Ordinal)).ToArray();
            foreach (var entry in moved) next.Remove(entry.Key);
            foreach (var entry in moved) next[key + entry.Key[sourceKey.Length..]] = entry.Value;
            return true;
        });
    }
    public void ReplaceFile(string source, string destination, string? backup)
    {
        var sourceKey = VirtualPath.Resolve(Mount, source);
        var backupKey = backup == null ? null : VirtualPath.Resolve(Mount, backup);
        Mutate("replace:" + JsonSerializer.Serialize(new[] { sourceKey, backupKey }), destination, (next, key) =>
        {
            RequireWritable(source);
            RequireWritable(destination);
            if (backup != null) RequireWritable(backup);
            var bytes = Read(next, sourceKey);
            var old = Read(next, key);
            if (key == sourceKey || backupKey == key || backupKey == sourceKey) throw new IOException("Replace paths must be distinct.");
            if (backupKey != null)
            {
                RequireParent(next, backupKey);
                if (next.TryGetValue(backupKey, out var value) && value == null) throw new UnauthorizedAccessException();
                next[backupKey] = old;
            }
            next[key] = bytes;
            next.Remove(sourceKey);
            return true;
        });
    }
    protected virtual void DisposeCore() { }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            DisposeCore();
            disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

public sealed class InMemoryFileSystem : VersionedFileSystem
{
    public InMemoryFileSystem(string mount = "/v") : base(mount) =>
        Initialize(new() { ["/"] = null });
    internal InMemoryFileSystem(string mount, Dictionary<string, byte[]?> state, Dictionary<string, FileAttributes>? attributes = null) : base(mount) =>
        Initialize(state, attributes: attributes);
}
