using System.Text;

namespace ManagedDependency;

public static class Scenarios
{
    private static FileInfo? lastFile;
    private static readonly List<FileInfo> files = [];
    private static FileInfo Identity(FileInfo file) => file;
    private static Task<FileInfo> IdentityAsync(FileInfo file) => Task.FromResult(file);
    public static FileInfo GetLastFile() => lastFile ?? throw new InvalidOperationException("Run scenarios first.");
    private static string InfoName<T>(T info) where T : FileSystemInfo => info.Name;
    private sealed class InfoList : List<FileInfo> { }
    private static int assertions;
    private static void Check(bool value, string name)
    {
        assertions++;
        if (!value) throw new InvalidOperationException("Managed compatibility assertion failed: " + name);
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { assertions++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    public static async Task<int> Run(string root)
    {
        assertions = 0;
        DirectoryInfo directory = Directory.CreateDirectory(root + "/nested");
        directory.CreateSubdirectory("deep");
        Check(directory.Exists && directory.Name == "nested", "directory info");
        Check(directory.Parent!.FullName == root, "parent");
        var path = root + "/nested/file.txt";
        lastFile = new FileInfo(path);
        files.Add(lastFile);
        Check(ReferenceEquals(Identity(lastFile), await IdentityAsync(lastFile)), "generic signatures");
        var infoList = new InfoList { lastFile };
        Check(InfoName(infoList[0]) == "file.txt", "generic base and constraint");
        using (StreamWriter writer = lastFile.CreateText()) await writer.WriteAsync("alpha");
        Check(lastFile.Exists && lastFile.Length == 5 && lastFile.Extension == ".txt", "file metadata");
        using (var writer = new StreamWriter(path, true, new UTF8Encoding(false))) await writer.WriteLineAsync(" beta");
        using (var reader = new StreamReader(path)) Check((await reader.ReadToEndAsync()).Contains("alpha beta"), "text constructors");
        FileSystemInfo info = files[^1];
        Check(info.FullName == path && info.Name == "file.txt", "base info");
        info.Refresh();
        lastFile.IsReadOnly = true;
        Throws<UnauthorizedAccessException>(() => File.WriteAllText(path, "forbidden"));
        lastFile.IsReadOnly = false;
        await File.WriteAllTextAsync(path, "snow \u96ea", Encoding.Unicode);
        Check(await File.ReadAllTextAsync(path) == "snow \u96ea", "async encoding");
        await File.AppendAllTextAsync(path, "!", Encoding.Unicode);
        Check((await File.ReadAllTextAsync(path)).EndsWith('!'), "async append");
        var copy = lastFile.CopyTo(root + "/nested/copy.txt");
        Check(copy.Length == lastFile.Length, "copy");
        var before = File.ReadAllBytes(copy.FullName);
        Throws<IOException>(() => File.Copy(path, copy.FullName));
        Check(File.ReadAllBytes(copy.FullName).SequenceEqual(before), "failed copy preserved destination");
        copy.MoveTo(root + "/nested/moved.txt");
        Check(copy.Exists && copy.Name == "moved.txt", "move");
        File.WriteAllText(root + "/nested/new.txt", "replacement");
        File.Replace(root + "/nested/new.txt", copy.FullName, root + "/nested/backup.txt");
        Check(File.ReadAllText(copy.FullName) == "replacement", "replace");
        Check(File.ReadAllBytes(root + "/nested/backup.txt").SequenceEqual(before), "replace backup");
        File.WriteAllText(root + "/nested/deep/deep.txt", "deep");
        File.WriteAllText(root + "/nested/deep/other.bin", "other");
        Check(Directory.GetFiles(root + "/nested", "*.txt", SearchOption.AllDirectories).Length == 4, "recursive wildcard");
        Check(directory.EnumerateFiles("*.txt", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).Count() == 4, "options and info enumeration");
        Check(directory.GetFileSystemInfos().Any(i => i is DirectoryInfo), "info array type");
        Check(directory.EnumerateDirectories("*", SearchOption.AllDirectories).Single().Name == "deep", "recursive directories");
        directory.MoveTo(root + "/renamed");
        Check(directory.Exists && Directory.Exists(root + "/renamed/deep"), "directory move");
        path = root + "/stream.bin";
        using (FileStream stream = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, true))
        {
            Stream baseStream = stream;
            Check(stream.CanRead && stream.CanWrite && stream.CanSeek && stream.IsAsync, "capabilities");
            stream.Write(new byte[] { 1, 2, 3 }.AsSpan());
            Check(stream.Length == 3 && stream.Position == 3, "span write length");
            stream.Seek(1, SeekOrigin.Begin);
            stream.WriteByte(9);
            await stream.FlushAsync();
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Check(reader.Length == 3, "reader sees flush");
                byte[] bytes = new byte[3];
                Check(await reader.ReadAsync(bytes.AsMemory()) == 3 && bytes.SequenceEqual(new byte[] { 1, 9, 3 }), "memory read");
                Throws<NotSupportedException>(() => reader.WriteByte(4));
            }
            baseStream.Position = 0;
            byte[] read = new byte[3];
            baseStream.ReadExactly(read);
            Check(read.SequenceEqual(new byte[] { 1, 9, 3 }), "inherited stream ReadExactly");
            stream.SetLength(5);
            Check(stream.Length == 5, "setlength extends");
            stream.Position = 4;
            await stream.WriteAsync(new byte[] { 7 }, 0, 1);
            stream.Flush(true);
            Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None));
        }
        await using (var stream = File.Open(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read }))
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            Check(output.ToArray().SequenceEqual(new byte[] { 1, 9, 3, 0, 7 }), "CopyToAsync");
        }
        using (var stream = new FileStream(path, FileMode.Append))
        {
            Throws<IOException>(() => stream.Seek(0, SeekOrigin.Begin));
            stream.WriteByte(8);
        }
        Check(File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 9, 3, 0, 7, 8 }), "append origin");
        Throws<IOException>(() => new FileStream(path, FileMode.CreateNew));
        Throws<ArgumentException>(() => new FileStream(path, FileMode.Create, FileAccess.Read));
        Throws<FileNotFoundException>(() => File.OpenRead(root + "/missing"));
        var disposed = File.OpenRead(path);
        disposed.Dispose();
        Throws<ObjectDisposedException>(() => disposed.ReadByte());
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            try { await File.WriteAllBytesAsync(root + "/canceled", new byte[] { 0 }, cts.Token); }
            catch (OperationCanceledException) { assertions++; }
            Check(!File.Exists(root + "/canceled"), "canceled write no file");
        }
        File.WriteAllBytes(root + "/span", new byte[] { 1, 2 }.AsSpan());
        File.AppendAllBytes(root + "/span", new byte[] { 3 }.AsSpan());
        Check(File.ReadAllBytes(root + "/span").SequenceEqual(new byte[] { 1, 2, 3 }), "byte spans");
        await File.WriteAllLinesAsync(root + "/lines", new[] { "a", "b" });
        Check((await File.ReadAllLinesAsync(root + "/lines")).SequenceEqual(new[] { "a", "b" }), "lines async");
        File.Delete(root + "/span");
        Check(!File.Exists(root + "/span"), "delete");
        Directory.Delete(root + "/renamed", true);
        Check(!Directory.Exists(root + "/renamed"), "recursive delete");
        return assertions;
    }
}
