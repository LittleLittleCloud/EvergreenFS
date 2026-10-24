using System.Text.Json;
using EvergreenFS.Rewriting;

if (args.Length == 1 && args[0] == "--inventory")
{
    Console.WriteLine(JsonSerializer.Serialize(RewriteEngine.Inventory(), new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
if (args.Length is < 1 or > 2 || args.Length == 2 && args[1] != "--closure")
{
    Console.Error.WriteLine("Usage: EvergreenFS.Rewriter <application.dll> [--closure] | --inventory");
    return 2;
}
try
{
    RewriteEngine.Rewrite(Path.GetFullPath(args[0]), args.Length == 2);
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or NotSupportedException)
{
    Console.Error.WriteLine($"error EGFS002: {error.Message}");
    return 1;
}
