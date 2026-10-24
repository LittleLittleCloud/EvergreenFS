using EvergreenFS;

using var provider = new InMemoryFileSystem("/evergreen-demo");
using var scope = VirtualFileSystem.Use(provider);
Directory.CreateDirectory("/evergreen-demo/notes");
File.WriteAllText("/evergreen-demo/notes/hello.txt", "isolated System.IO");
var first = provider.History[^1].Id;
File.WriteAllText("/evergreen-demo/notes/hello.txt", "changed");
Console.WriteLine(File.ReadAllText("/evergreen-demo/notes/hello.txt"));
Console.WriteLine(System.Text.Encoding.UTF8.GetString(provider.Snapshot(first).ReadAllBytes("/evergreen-demo/notes/hello.txt")));
Console.WriteLine(string.Join(", ", Directory.GetFiles("/evergreen-demo/notes")));
Console.WriteLine($"enumerated={Directory.EnumerateFiles("/evergreen-demo/notes").Count()}");
Console.WriteLine($"provider={provider.GetType().Name}; revisions={provider.History.Count}; isolated=True");
