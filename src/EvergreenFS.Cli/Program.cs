using System.Text;
using EvergreenFS;

if (args.Length == 1 && args[0] == "demo")
{
    using var memory = new InMemoryFileSystem();
    using var scope = VirtualFileSystem.Use(memory);
    DirectoryFacade.CreateDirectory("/v/notes");
    FileFacade.WriteAllText("/v/notes/hello.txt", "first");
    var revision = memory.History[^1].Id;
    FileFacade.WriteAllText("/v/notes/hello.txt", "second");
    Console.WriteLine($"live: {FileFacade.ReadAllText("/v/notes/hello.txt")}");
    Console.WriteLine($"revision {revision}: {Encoding.UTF8.GetString(memory.Snapshot(revision).ReadAllBytes("/v/notes/hello.txt"))}");
    using var fork = memory.Fork(revision);
    fork.WriteAllBytes("/v/notes/hello.txt", Encoding.UTF8.GetBytes("fork"));
    Console.WriteLine($"fork: {Encoding.UTF8.GetString(fork.ReadAllBytes("/v/notes/hello.txt"))}");
    Console.WriteLine($"original: {FileFacade.ReadAllText("/v/notes/hello.txt")}; history={memory.History.Count}");
    return 0;
}
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: EvergreenFS.Cli demo | <database> tree|list|history|read|write|mkdir|fork [arguments]. See README.");
    return 2;
}
try
{
    using var fs = new SqliteFileSystem(args[0]);
    switch (args[1])
    {
        case "tree" when args.Length == 2:
            foreach (var path in fs.Snapshot().Paths) Console.WriteLine(path);
            break;
        case "list" when args.Length is 2 or 3:
            var directory = args.Length == 3 ? args[2] : "/v";
            foreach (var path in fs.GetDirectories(directory)) Console.WriteLine($"D {path}");
            foreach (var path in fs.GetFiles(directory)) Console.WriteLine($"F {path}");
            break;
        case "history" when args.Length == 2:
            foreach (var revision in fs.History)
                Console.WriteLine($"{revision.Id}\t{revision.Operation}\t{revision.Path}\t{revision.Timestamp:O}");
            break;
        case "read" when args.Length is 3 or 4:
            var bytes = args.Length == 4
                ? fs.Snapshot(long.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)).ReadAllBytes(args[2])
                : fs.ReadAllBytes(args[2]);
            Console.WriteLine(Encoding.UTF8.GetString(bytes));
            break;
        case "write" when args.Length == 4:
            fs.WriteAllBytes(args[2], Encoding.UTF8.GetBytes(args[3]));
            break;
        case "mkdir" when args.Length == 3: fs.CreateDirectory(args[2]); break;
        case "fork" when args.Length == 4:
            fs.ExportFork(long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), args[3]);
            Console.WriteLine($"Forked revision {args[2]} into {args[3]} (independent history).");
            break;
        default: throw new ArgumentException("Unknown command or wrong argument count. See README.");
    }
    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or Microsoft.Data.Sqlite.SqliteException)
{
    Console.Error.WriteLine($"EvergreenFS: {exception.Message}");
    return 1;
}
