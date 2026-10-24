using EvergreenFS;
using Xunit;

namespace EvergreenFS.Tests;

public class StreamAndMetadataTests
{
    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("native")]
    public void StreamFailuresFlushSnapshotsAndHandleLifetimes(string kind)
    {
        using var workspace = new Workspace();
        using var fs = workspace.Provider(kind);
        using var scope = VirtualFileSystem.Use(fs);
        fs.WriteAllBytes("/v/f", [1, 2]);
        var original = fs.Snapshot();
        var count = fs.History.Count;
        Assert.Throws<IOException>(() => new ManagedFileStream("/v/f", FileMode.CreateNew));
        Assert.Throws<ArgumentException>(() => new ManagedFileStream("/v/f", FileMode.Append, FileAccess.ReadWrite));
        Assert.Throws<NotSupportedException>(() => new ManagedFileStream("/v/f", FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose));
        Assert.Throws<NotSupportedException>(() => new ManagedFileStream("/v/f", FileMode.Open, FileAccess.Read, FileShare.Inheritable));
        Assert.Throws<DirectoryNotFoundException>(() => new ManagedFileStream("/v/missing/f", FileMode.Create));
        Assert.Equal(count, fs.History.Count);
        using (var stream = new ManagedFileStream("/v/f", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var position = stream.Position;
            Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
            Assert.Throws<ArgumentOutOfRangeException>(() => stream.SetLength(-1));
            Assert.Throws<ArgumentException>(() => stream.Write([1], 1, 1));
            Assert.Throws<ArgumentNullException>(() => { _ = stream.ReadAsync(null!, 0, 0); });
            Assert.Throws<ArgumentNullException>(() => { _ = stream.WriteAsync(null!, 0, 0); });
            Assert.Equal(position, stream.Position);
            Assert.Equal(count, fs.History.Count);
            Assert.Throws<IOException>(() => FileFacade.WriteAllBytes("/v/f", [9]));
            Assert.Throws<IOException>(() => FileFacade.Delete("/v/f"));
            Assert.Throws<IOException>(() => FileFacade.Move("/v/f", "/v/moved"));
            Assert.Throws<IOException>(() => DirectoryFacade.Delete("/v", true));
            Assert.Equal(count, fs.History.Count);
            using var reader = new ManagedFileStream("/v/f", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.SetLength(0);
            stream.Write([3, 4, 5]);
            Assert.Equal(new byte[] { 1, 2 }, fs.ReadAllBytes("/v/f"));
            Assert.Equal(2, reader.Length);
            Assert.Equal(count, fs.History.Count);
            stream.Flush();
            Assert.Equal(count + 1, fs.History.Count);
            Assert.Equal(3, reader.Length);
            Assert.Equal(new byte[] { 1, 2 }, original.ReadAllBytes("/v/f"));
            stream.Flush();
            Assert.Equal(count + 1, fs.History.Count);
            stream.Position = 1;
            stream.WriteByte(9);
        }
        Assert.Equal(count + 2, fs.History.Count);
        Assert.Equal(new byte[] { 3, 9, 5 }, fs.ReadAllBytes("/v/f"));
        using var fork = fs.Fork(original.Revision);
        Assert.Equal(new byte[] { 1, 2 }, fork.ReadAllBytes("/v/f"));
        Assert.Equal(new byte[] { 1, 2 }, original.ReadAllBytes("/v/f"));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("native")]
    public async Task CancellationAndInvalidTextLeaveNoHistory(string kind)
    {
        using var workspace = new Workspace();
        using var fs = workspace.Provider(kind);
        using var scope = VirtualFileSystem.Use(fs);
        fs.WriteAllBytes("/v/f", [1]);
        var count = fs.History.Count;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileFacade.WriteAllBytesAsync("/v/f", new byte[] { 2 }, cancellation.Token));
        Assert.Throws<ArgumentNullException>(() => FileFacade.WriteAllText("/v/f", "test", null!));
        Assert.Throws<System.Text.EncoderFallbackException>(() => FileFacade.WriteAllText("/v/f", "\ud800", new System.Text.UTF8Encoding(false, true)));
        Assert.Throws<System.Text.EncoderFallbackException>(() => FileFacade.AppendAllText("/v/f", "\ud800", new System.Text.UTF8Encoding(false, true)));
        using (var stream = FileFacade.Open("/v/f", FileMode.Open, FileAccess.Write))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.WriteAsync(new byte[] { 2 }.AsMemory(), cancellation.Token).AsTask());
        Assert.Equal(count, fs.History.Count);
        Assert.Equal(new byte[] { 1 }, fs.ReadAllBytes("/v/f"));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("native")]
    public void AttributesAreImmutableAcrossSnapshotsCopiesMovesAndForks(string kind)
    {
        using var workspace = new Workspace();
        using var fs = workspace.Provider(kind);
        fs.CreateDirectory("/v/a");
        fs.WriteAllBytes("/v/a/f", [1]);
        fs.SetAttributes("/v/a/f", FileAttributes.ReadOnly);
        using (VirtualFileSystem.Use(fs))
        {
            var info = new ManagedFileInfo("/v/a/f");
            info.IsReadOnly = false;
            info.IsReadOnly = true;
            Assert.Equal(FileAttributes.ReadOnly, info.Attributes);
        }
        var snapshot = fs.Snapshot();
        var count = fs.History.Count;
        Assert.Throws<UnauthorizedAccessException>(() => fs.WriteAllBytes("/v/a/f", [2]));
        Assert.Equal(count, fs.History.Count);
        using var fork = fs.Fork(snapshot.Revision);
        Assert.True((fork.GetAttributes("/v/a/f") & FileAttributes.ReadOnly) != 0);
        fs.CopyFile("/v/a/f", "/v/a/copy");
        Assert.True((fs.GetAttributes("/v/a/copy") & FileAttributes.ReadOnly) != 0);
        fs.SetAttributes("/v/a/f", FileAttributes.Normal);
        fs.MoveDirectory("/v/a", "/v/b");
        Assert.True((fs.GetAttributes("/v/b/copy") & FileAttributes.ReadOnly) != 0);
        Assert.Equal(FileAttributes.ReadOnly, snapshot.GetAttributes("/v/a/f"));
        Assert.Throws<UnauthorizedAccessException>(() => fork.WriteAllBytes("/v/a/f", [3]));
        fs.SetAttributes("/v/b/copy", FileAttributes.Normal);
        fs.DeleteDirectory("/v/b", true);
    }

    [Fact]
    public void SqliteMetadataSurvivesReopenAndPersistentFork()
    {
        using var workspace = new Workspace();
        var database = Path.Combine(workspace.Root, "state.db");
        var forkPath = Path.Combine(workspace.Root, "fork.db");
        using (var fs = new SqliteFileSystem(database))
        {
            fs.WriteAllBytes("/v/f", [1]);
            fs.SetAttributes("/v/f", FileAttributes.ReadOnly);
            fs.ExportFork(fs.History[^1].Id, forkPath);
        }
        foreach (var path in new[] { database, forkPath })
        {
            using var fs = new SqliteFileSystem(path);
            Assert.Equal(FileAttributes.ReadOnly, fs.GetAttributes("/v/f"));
            Assert.Throws<UnauthorizedAccessException>(() => fs.WriteAllBytes("/v/f", [2]));
            fs.SetAttributes("/v/f", FileAttributes.Normal);
            fs.WriteAllBytes("/v/f", [2]);
        }
    }

    [Fact]
    public void WholeFileFacadePublishesExactlyOneRevisionAndDatabaseFailurePublishesNone()
    {
        using var workspace = new Workspace();
        var database = Path.Combine(workspace.Root, "state.db");
        using var fs = new SqliteFileSystem(database);
        using var scope = VirtualFileSystem.Use(fs);
        FileFacade.WriteAllBytes("/v/f", [1]);
        Assert.Equal(2, fs.History.Count);
        FileFacade.AppendAllBytes("/v/f", [2]);
        Assert.Equal(3, fs.History.Count);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_revision BEFORE INSERT ON revisions BEGIN SELECT RAISE(ABORT, 'injected transaction failure'); END";
        command.ExecuteNonQuery();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => FileFacade.WriteAllBytes("/v/f", [9]));
        Assert.Equal(3, fs.History.Count);
        Assert.Equal(new byte[] { 1, 2 }, fs.ReadAllBytes("/v/f"));
        using var reopened = new SqliteFileSystem(database);
        Assert.Equal(3, reopened.History.Count);
        Assert.Equal(new byte[] { 1, 2 }, reopened.ReadAllBytes("/v/f"));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("native")]
    public void FailedDisposeFlushDoesNotPublishBytesAndReleasesLease(string kind)
    {
        using var workspace = new Workspace();
        using var fs = workspace.Provider(kind);
        using var scope = VirtualFileSystem.Use(fs);
        fs.WriteAllBytes("/v/f", [1]);
        var stream = new ManagedFileStream("/v/f", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.WriteByte(9);
        fs.SetAttributes("/v/f", FileAttributes.ReadOnly);
        var count = fs.History.Count;
        Assert.Throws<UnauthorizedAccessException>(() => stream.Dispose());
        Assert.Equal(count, fs.History.Count);
        Assert.Equal(new byte[] { 1 }, fs.ReadAllBytes("/v/f"));
        fs.SetAttributes("/v/f", FileAttributes.Normal);
        FileFacade.WriteAllBytes("/v/f", [3]);
        Assert.Equal(new byte[] { 3 }, fs.ReadAllBytes("/v/f"));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("native")]
    public void FailedCopyMoveReplaceAreAtomicAndRevisionedOnce(string kind)
    {
        using var workspace = new Workspace();
        using var fs = workspace.Provider(kind);
        fs.WriteAllBytes("/v/source", [1]);
        fs.WriteAllBytes("/v/dest", [2]);
        var snapshot = fs.Snapshot();
        var count = fs.History.Count;
        Assert.Throws<IOException>(() => fs.CopyFile("/v/source", "/v/dest"));
        Assert.Throws<IOException>(() => fs.MoveFile("/v/source", "/v/dest"));
        Assert.Throws<DirectoryNotFoundException>(() => fs.ReplaceFile("/v/source", "/v/dest", "/v/missing/backup"));
        Assert.Equal(count, fs.History.Count);
        Assert.Equal(new byte[] { 1 }, fs.ReadAllBytes("/v/source"));
        Assert.Equal(new byte[] { 2 }, fs.ReadAllBytes("/v/dest"));
        fs.ReplaceFile("/v/source", "/v/dest", "/v/backup|name");
        Assert.Equal(count + 1, fs.History.Count);
        Assert.False(fs.FileExists("/v/source"));
        Assert.Equal(new byte[] { 1 }, fs.ReadAllBytes("/v/dest"));
        Assert.Equal(new byte[] { 2 }, fs.ReadAllBytes("/v/backup|name"));
        Assert.Equal(new byte[] { 2 }, snapshot.ReadAllBytes("/v/dest"));
    }
}
