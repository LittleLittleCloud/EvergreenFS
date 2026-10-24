using System.Text;
using EvergreenFS;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EvergreenFS.Tests;

public sealed class Workspace : IDisposable
{
    public static string Repository
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "EvergreenFS.sln"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Cannot locate repository.");
        }
    }
    public string Root { get; } = Path.Combine(Repository, "artifacts", "tests", Guid.NewGuid().ToString("N"));
    public Workspace() => Directory.CreateDirectory(Root);
    public IVirtualFileSystem Provider(string kind) => kind switch
    {
        "memory" => new InMemoryFileSystem(),
        "sqlite" => new SqliteFileSystem(Path.Combine(Root, "state.db")),
        "native" => new OriginalFileSystem(Path.Combine(Root, "native")),
        _ => throw new ArgumentException("Unknown provider.")
    };
    public void Dispose() => Directory.Delete(Root, true);
}

public class FileSystemTests
{
    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("native")]
    public void HistorySnapshotsForksAndFailures(string kind)
    {
        using var workspace = new Workspace();
        using var fs = workspace.Provider(kind);
        Assert.Single(fs.History);
        Assert.Throws<DirectoryNotFoundException>(() => fs.WriteAllBytes("/v/missing/file", [1]));
        Assert.Throws<DirectoryNotFoundException>(() => fs.DeleteFile("/v/missing/file"));
        Assert.Throws<DirectoryNotFoundException>(() => fs.DeleteDirectory("/v/missing"));
        Assert.Single(fs.History);
        fs.CreateDirectory("/v/a");
        fs.CreateDirectory("/v/a");
        fs.DeleteFile("/v/a/absent");
        Assert.Equal(2, fs.History.Count);
        byte[] input = [1, 2, 3];
        fs.WriteAllBytes("/v/a/f", input);
        input[0] = 99;
        var revision = fs.History[^1].Id;
        var snapshot = fs.Snapshot(revision);
        var output = fs.ReadAllBytes("/v/a/f");
        output[0] = 99;
        var old = snapshot.ReadAllBytes("/v/a/f");
        old[0] = 99;
        Assert.Equal(new byte[] { 1, 2, 3 }, fs.ReadAllBytes("/v/a/f"));
        using var fork = fs.Fork(revision);
        fork.WriteAllBytes("/v/a/f", [4]);
        fs.WriteAllBytes("/v/a/f", [5]);
        Assert.Equal(new byte[] { 1, 2, 3 }, snapshot.ReadAllBytes("/v/a/f"));
        Assert.Equal(new byte[] { 4 }, fork.ReadAllBytes("/v/a/f"));
        Assert.Equal(new byte[] { 5 }, fs.ReadAllBytes("/v/a/f"));
        var count = fs.History.Count;
        Assert.Throws<IOException>(() => fs.DeleteDirectory("/v/a"));
        Assert.Throws<DirectoryNotFoundException>(() => fs.CreateDirectory("/v/a/f/child"));
        Assert.Throws<UnauthorizedAccessException>(() => fs.WriteAllBytes("/v/a", [0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => fs.Snapshot(999));
        Assert.Equal(count, fs.History.Count);
        fs.DeleteDirectory("/v/a", true);
        Assert.False(fs.FileExists("/v/a/f"));
        Assert.Equal(new byte[] { 1, 2, 3 }, snapshot.ReadAllBytes("/v/a/f"));
        Assert.Equal(Enumerable.Range(0, fs.History.Count).Select(i => (long)i), fs.History.Select(r => r.Id));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("native")]
    public void PathsCannotEscapeOrFallBackToHost(string kind)
    {
        using var workspace = new Workspace();
        File.WriteAllText(Path.Combine(workspace.Root, "secret"), "host only");
        using var fs = workspace.Provider(kind);
        Assert.Throws<UnauthorizedAccessException>(() => fs.ReadAllBytes(Path.Combine(workspace.Root, "secret")));
        foreach (var path in new[] { "../secret", "/v/../secret", "/v/a/../../secret", "/v2/secret" })
        {
            Assert.Throws<UnauthorizedAccessException>(() => fs.WriteAllBytes(path, [1]));
            Assert.False(fs.FileExists(path));
        }
        Assert.Throws<ArgumentException>(() => fs.WriteAllBytes("C:\\secret", [1]));
        fs.WriteAllBytes("relative", [2]);
        Assert.Equal(new byte[] { 2 }, fs.ReadAllBytes("/v/a/../relative"));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "relative")));
        Assert.Equal("host only", File.ReadAllText(Path.Combine(workspace.Root, "secret")));
        Assert.False(fs.FileExists(null));
        Assert.False(fs.DirectoryExists(""));
    }

    [Fact]
    public void SqliteReopensWithHistoryAndIndependentPersistentFork()
    {
        using var workspace = new Workspace();
        var database = Path.Combine(workspace.Root, "state.db");
        var destination = Path.Combine(workspace.Root, "fork.db");
        using (var fs = new SqliteFileSystem(database))
        {
            fs.WriteAllBytes("/v/f", [1]);
            fs.WriteAllBytes("/v/f", [2]);
        }
        using (var fs = new SqliteFileSystem(database))
        {
            Assert.Equal(3, fs.History.Count);
            Assert.Equal(new byte[] { 1 }, fs.Snapshot(1).ReadAllBytes("/v/f"));
            Assert.Equal(new byte[] { 2 }, fs.ReadAllBytes("/v/f"));
            fs.ExportFork(1, destination);
            using var fork = new SqliteFileSystem(destination);
            fork.WriteAllBytes("/v/f", [3]);
            Assert.Equal(new byte[] { 2 }, fs.ReadAllBytes("/v/f"));
        }
        using var reopened = new SqliteFileSystem(destination);
        Assert.Equal(new byte[] { 3 }, reopened.ReadAllBytes("/v/f"));
        Assert.Throws<ArgumentException>(() => new SqliteFileSystem(database, "/other"));
    }

    [Fact]
    public void StaleSqliteWriterFailsWithoutPublishingHistory()
    {
        using var workspace = new Workspace();
        var database = Path.Combine(workspace.Root, "state.db");
        using var first = new SqliteFileSystem(database);
        using var stale = new SqliteFileSystem(database);
        first.WriteAllBytes("/v/a", [1]);
        Assert.Throws<SqliteException>(() => stale.WriteAllBytes("/v/b", [2]));
        Assert.Single(stale.History);
        Assert.False(stale.FileExists("/v/b"));
        using var reopened = new SqliteFileSystem(database);
        Assert.Equal(2, reopened.History.Count);
        Assert.False(reopened.FileExists("/v/b"));
    }

    [Fact]
    public void NativeWritesArePhysicalAndLinksAreRejected()
    {
        using var workspace = new Workspace();
        var root = Path.Combine(workspace.Root, "native");
        using (var fs = new OriginalFileSystem(root))
        {
            fs.CreateDirectory("/v/sub");
            fs.WriteAllBytes("/v/sub/file", [3]);
            Assert.Equal(new byte[] { 3 }, File.ReadAllBytes(Path.Combine(root, "sub", "file")));
        }
        using (var reopened = new OriginalFileSystem(root))
        {
            Assert.Equal(new byte[] { 3 }, reopened.ReadAllBytes("/v/sub/file"));
            Assert.Single(reopened.History);
        }
        var link = Path.Combine(root, "link");
        File.CreateSymbolicLink(link, Path.Combine(root, "sub", "file"));
        Assert.Throws<IOException>(() => new OriginalFileSystem(root));
    }

    [Fact]
    public void NativeFailureDoesNotPublishRevisionOrWriteThroughSymlink()
    {
        using var workspace = new Workspace();
        var root = Path.Combine(workspace.Root, "native");
        var outside = Path.Combine(workspace.Root, "outside");
        File.WriteAllBytes(outside, [7]);
        using var fs = new OriginalFileSystem(root);
        File.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        Assert.Throws<IOException>(() => fs.WriteAllBytes("/v/link", [9]));
        Assert.Single(fs.History);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(outside));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("native")]
    public void DisposedProvidersFailExplicitly(string kind)
    {
        using var workspace = new Workspace();
        var fs = workspace.Provider(kind);
        fs.Dispose();
        fs.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fs.ReadAllBytes("/v/f"));
        Assert.Throws<ObjectDisposedException>(() => fs.FileExists("/v/f"));
    }

    [Fact]
    public void ScopeHasNoImplicitNativeFallbackAndRestoresPreviousProvider()
    {
        Assert.Throws<InvalidOperationException>(() => FileFacade.Exists("/v/f"));
        using var first = new InMemoryFileSystem();
        using var second = new InMemoryFileSystem();
        using (VirtualFileSystem.Use(first))
        {
            FileFacade.WriteAllText("/v/f", "first");
            using (VirtualFileSystem.Use(second)) Assert.False(FileFacade.Exists("/v/f"));
            Assert.Equal("first", FileFacade.ReadAllText("/v/f"));
        }
        Assert.Throws<InvalidOperationException>(() => FileFacade.Exists("/v/f"));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("native")]
    public void LocallyAuthoredDifferentialCompatibility(string kind)
    {
        using var workspace = new Workspace();
        using var fs = workspace.Provider(kind);
        var baseline = Path.Combine(workspace.Root, "baseline");
        Directory.CreateDirectory(baseline);
        var expected = CompatibilityTrace(baseline, native: true);
        using var scope = VirtualFileSystem.Use(fs);
        Assert.Equal(expected, CompatibilityTrace("/v", native: false));
    }

    private static string[] CompatibilityTrace(string root, bool native)
    {
        var trace = new List<string>();
        string P(string name) => root + "/" + name;
        void Mk(string p) { if (native) Directory.CreateDirectory(p); else DirectoryFacade.CreateDirectory(p); }
        void Write(string p, byte[] b) { if (native) File.WriteAllBytes(p, b); else FileFacade.WriteAllBytes(p, b); }
        byte[] Read(string p) => native ? File.ReadAllBytes(p) : FileFacade.ReadAllBytes(p);
        bool Exists(string? p) => native ? File.Exists(p) : FileFacade.Exists(p);
        void Delete(string p) { if (native) File.Delete(p); else FileFacade.Delete(p); }
        void Rm(string p, bool recursive) { if (native) Directory.Delete(p, recursive); else DirectoryFacade.Delete(p, recursive); }
        void Error(Action action)
        {
            var exception = Record.Exception(action);
            trace.Add(exception?.GetType().Name ?? "NO ERROR");
        }
        trace.Add(Exists(null).ToString());
        trace.Add(Exists(P("absent")).ToString());
        Error(() => Read(P("absent")));
        Error(() => Read(P("missing/file")));
        Error(() => Write(P("missing/file"), [1]));
        Error(() => Delete(P("missing/file")));
        Delete(P("absent"));
        Mk(P("a/nested"));
        Mk(P("a"));
        Write(P("a/bytes"), [0, 1, 127, 255]);
        trace.Add(Convert.ToHexString(Read(P("a/bytes"))));
        Write(P("a/bytes"), [9]);
        trace.Add(Convert.ToHexString(Read(P("a/bytes"))));
        foreach (var encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            if (native) File.WriteAllText(P("a/text"), "hello \u00e9 \u96ea", encoding);
            else FileFacade.WriteAllText(P("a/text"), "hello \u00e9 \u96ea", encoding);
            trace.Add(Convert.ToHexString(Read(P("a/text"))));
            trace.Add(native ? File.ReadAllText(P("a/text")) : FileFacade.ReadAllText(P("a/text")));
            trace.Add(native ? File.ReadAllText(P("a/text"), encoding) : FileFacade.ReadAllText(P("a/text"), encoding));
        }
        if (native) File.WriteAllText(P("a/default"), null);
        else FileFacade.WriteAllText(P("a/default"), null);
        trace.Add(Convert.ToHexString(Read(P("a/default"))));
        Error(() =>
        {
            if (native) File.WriteAllText(P("a/invalid-unicode"), "\ud800");
            else FileFacade.WriteAllText(P("a/invalid-unicode"), "\ud800");
        });
        Delete(P("a/invalid-unicode"));
        trace.Add((native ? Directory.Exists(P("a")) : DirectoryFacade.Exists(P("a"))).ToString());
        trace.Add(Exists(P("a")).ToString());
        trace.Add((native ? Directory.Exists(P("a/bytes")) : DirectoryFacade.Exists(P("a/bytes"))).ToString());
        string Names(IEnumerable<string> paths) => string.Join(",", paths.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        trace.Add(Names(native ? Directory.GetFiles(P("a")) : DirectoryFacade.GetFiles(P("a"))));
        trace.Add(Names(native ? Directory.GetDirectories(P("a")) : DirectoryFacade.GetDirectories(P("a"))));
        trace.Add(Names(native ? Directory.EnumerateFiles(P("a")) : DirectoryFacade.EnumerateFiles(P("a"))));
        trace.Add(Names(native ? Directory.EnumerateDirectories(P("a")) : DirectoryFacade.EnumerateDirectories(P("a"))));
        Error(() => Write(P("a"), [1]));
        Error(() => Read(P("a")));
        Error(() => Mk(P("a/bytes/child")));
        Error(() => Rm(P("a"), false));
        Error(() => Rm(P("absent"), false));
        Delete(P("a/bytes"));
        Delete(P("a/bytes"));
        if (native) Directory.Delete(P("a/nested"));
        else DirectoryFacade.Delete(P("a/nested"));
        Rm(P("a"), true);
        trace.Add((native ? Directory.Exists(P("a")) : DirectoryFacade.Exists(P("a"))).ToString());
        return trace.ToArray();
    }
}
