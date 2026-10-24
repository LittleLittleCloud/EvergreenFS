namespace EvergreenFS;

// The native provider owns its root exclusively during its lifetime.
public sealed class OriginalFileSystem : VersionedFileSystem
{
    private readonly string root;
    public OriginalFileSystem(string rootPath, string mount = "/v") : base(mount)
    {
        root = Path.GetFullPath(rootPath);
        CheckLinks(root);
        Directory.CreateDirectory(root);
        var entries = new Dictionary<string, byte[]?> { ["/"] = null };
        Scan(root, entries);
        var attributes = entries.ToDictionary(p => p.Key, p => File.GetAttributes(Path.Combine(root, p.Key.TrimStart('/'))), StringComparer.Ordinal);
        Initialize(entries, attributes: attributes);
    }
    private static void CheckLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if (current.LinkTarget != null) throw new IOException("Symbolic links are not supported by the native provider.");
    }
    private void Scan(string directory, Dictionary<string, byte[]?> entries)
    {
        foreach (var path in Directory.GetFileSystemEntries(directory))
        {
            if (new FileInfo(path).LinkTarget != null) throw new IOException("Symbolic links are not supported by the native provider.");
            var key = "/" + Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (Directory.Exists(path)) { entries[key] = null; Scan(path, entries); }
            else entries[key] = File.ReadAllBytes(path);
        }
    }
    protected override void Persist(Revision revision, Dictionary<string, byte[]?> next)
    {
        var key = VirtualPath.Resolve(Mount, revision.Path);
        var path = Path.Combine(root, key.TrimStart('/'));
        CheckLinks(path);
        switch (revision.Operation)
        {
            case "write": File.WriteAllBytes(path, next[key]!); break;
            case "mkdir": Directory.CreateDirectory(path); break;
            case "delete-file": File.Delete(path); break;
            case "delete-directory": Directory.Delete(path, true); break;
            default:
                if (revision.Operation.StartsWith("attributes:", StringComparison.Ordinal))
                    File.SetAttributes(path, (FileAttributes)int.Parse(revision.Operation[11..], System.Globalization.CultureInfo.InvariantCulture));
                else if (revision.Operation.StartsWith("copy:", StringComparison.Ordinal))
                    File.Copy(Physical(revision.Operation[5..]), path, true);
                else if (revision.Operation.StartsWith("move:", StringComparison.Ordinal))
                    File.Move(Physical(revision.Operation[5..]), path, true);
                else if (revision.Operation.StartsWith("move-directory:", StringComparison.Ordinal))
                    Directory.Move(Physical(revision.Operation[15..]), path);
                else if (revision.Operation.StartsWith("replace:", StringComparison.Ordinal))
                {
                    var parts = System.Text.Json.JsonSerializer.Deserialize<string?[]>(revision.Operation[8..])!;
                    File.Replace(Physical(parts[0]!), path, parts[1] == null ? null : Physical(parts[1]!));
                }
                else throw new InvalidOperationException("Unknown mutation.");
                break;
        }
    }
    private string Physical(string key)
    {
        var path = Path.Combine(root, key.TrimStart('/'));
        CheckLinks(path);
        return path;
    }
}
