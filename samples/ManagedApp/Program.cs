using EvergreenFS;
using ManagedDependency;

if (args.Length != 2) throw new ArgumentException("Usage: ManagedApp memory|sqlite|native <administrative-storage-path>");
using IVirtualFileSystem fs = args[0] switch
{
    "memory" => new InMemoryFileSystem(),
    "sqlite" => new SqliteFileSystem(args[1]),
    "native" => new OriginalFileSystem(args[1]),
    _ => throw new ArgumentException("Unknown provider.")
};
using var scope = VirtualFileSystem.Use(fs);
var checks = await Scenarios.Run("/v");
Console.WriteLine($"dependency-signature={Scenarios.GetLastFile().Name}");
Console.WriteLine($"managed-dependency={args[0]}; assertions={checks}; history={fs.History.Count}; isolated=True");
