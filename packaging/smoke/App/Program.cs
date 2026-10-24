using System.Text;
using EvergreenFS;
using PackageDependency;

if (args.Length != 3) throw new ArgumentException("Expected provider, virtual root, database path.");
var root = args[1];
using IVirtualFileSystem fs = args[0] == "memory"
    ? new InMemoryFileSystem(root) : new SqliteFileSystem(args[2], root);
using var scope = VirtualFileSystem.Use(fs);
Directory.CreateDirectory(root + "/app");
await File.WriteAllTextAsync(root + "/app/hello.txt", "first");
var revision = fs.History[^1].Id;
await File.WriteAllTextAsync(root + "/app/hello.txt", "second");
if (Encoding.UTF8.GetString(fs.Snapshot(revision).ReadAllBytes(root + "/app/hello.txt")) != "first")
    throw new Exception("Historical content was not preserved.");
var info = new FileInfo(root + "/app/hello.txt");
if (!info.Exists || info.Length != 6 || info.OpenText().ReadToEnd() != "second")
    throw new Exception("FileInfo did not read virtual data.");
await using (var stream = new FileStream(root + "/app/bytes.bin", FileMode.Create, FileAccess.ReadWrite))
{
    await stream.WriteAsync(new byte[] { 1, 2, 3 });
    await stream.FlushAsync();
    stream.Position = 0;
    var bytes = new byte[3];
    if (await stream.ReadAsync(bytes) != 3 || !bytes.SequenceEqual(new byte[] { 1, 2, 3 }))
        throw new Exception("FileStream async round trip failed.");
}
var dependencyInfo = await Operations.Write(root);
if (!dependencyInfo.Exists || dependencyInfo.OpenText().ReadToEnd() != "from package dependency")
    throw new Exception("Package dependency closure was not rewritten.");
if (!fs.FileExists(dependencyInfo.FullName) || Directory.GetFiles(root + "/app").Length != 2 ||
    !fs.ReadAllBytes(root + "/app/bytes.bin").SequenceEqual(new byte[] { 1, 2, 3 }) || fs.History.Count < 7)
    throw new Exception("Missing virtual provider data or mutation history.");
Console.WriteLine($"PASS package consumer {args[0]}: history={fs.History.Count}; dependency={dependencyInfo.Name}");
