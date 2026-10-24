namespace PackageDependency;

public static class Operations
{
    public static async Task<FileInfo> Write(string root)
    {
        Directory.CreateDirectory(root + "/dependency");
        var path = root + "/dependency/data.txt";
        await File.WriteAllTextAsync(path, "from package dependency");
        return new FileInfo(path);
    }
}
