namespace EvergreenFS;

public abstract class ManagedFileSystemInfo
{
    protected readonly IVirtualFileSystem Provider;
    protected string fullName;
    protected ManagedFileSystemInfo(string path)
    {
        Provider = VirtualFileSystem.Current;
        fullName = VirtualPath.Display(Provider.Mount, VirtualPath.Resolve(Provider.Mount, path));
    }
    protected T WithProvider<T>(Func<T> action)
    {
        using var scope = VirtualFileSystem.Use(Provider);
        return action();
    }
    protected void WithProvider(Action action) { using var scope = VirtualFileSystem.Use(Provider); action(); }
    public string FullName => fullName;
    public string Name => fullName == Provider.Mount ? Path.GetFileName(fullName.TrimEnd('/')) : Path.GetFileName(fullName);
    public string Extension => Path.GetExtension(fullName);
    public FileAttributes Attributes { get => Provider.GetAttributes(fullName); set => Provider.SetAttributes(fullName, value); }
    public abstract bool Exists { get; }
    public abstract void Delete();
    public void Refresh() { }
    public override string ToString() => fullName;
}

public sealed class ManagedFileInfo : ManagedFileSystemInfo
{
    public ManagedFileInfo(string fileName) : base(fileName) { }
    public override bool Exists => Provider.FileExists(fullName);
    public long Length => Provider.ReadAllBytes(fullName).LongLength;
    public bool IsReadOnly { get => (Attributes & FileAttributes.ReadOnly) != 0; set => Attributes = value ? Attributes | FileAttributes.ReadOnly : Attributes & ~FileAttributes.ReadOnly; }
    public string? DirectoryName => VirtualPath.Display(Provider.Mount, VirtualPath.Parent(VirtualPath.Resolve(Provider.Mount, fullName)));
    public ManagedDirectoryInfo? Directory => WithProvider(() => new ManagedDirectoryInfo(DirectoryName!));
    public override void Delete() => WithProvider(() => FileFacade.Delete(fullName));
    public ManagedFileStream Create() => WithProvider(() => FileFacade.Create(fullName));
    public ManagedFileStream Open(FileMode mode) => WithProvider(() => FileFacade.Open(fullName, mode));
    public ManagedFileStream Open(FileMode mode, FileAccess access) => WithProvider(() => FileFacade.Open(fullName, mode, access));
    public ManagedFileStream Open(FileMode mode, FileAccess access, FileShare share) => WithProvider(() => FileFacade.Open(fullName, mode, access, share));
    public ManagedFileStream Open(FileStreamOptions options) => WithProvider(() => FileFacade.Open(fullName, options));
    public ManagedFileStream OpenRead() => WithProvider(() => FileFacade.OpenRead(fullName));
    public ManagedFileStream OpenWrite() => WithProvider(() => FileFacade.OpenWrite(fullName));
    public ManagedStreamReader OpenText() => WithProvider(() => FileFacade.OpenText(fullName));
    public ManagedStreamWriter CreateText() => WithProvider(() => FileFacade.CreateText(fullName));
    public ManagedStreamWriter AppendText() => WithProvider(() => FileFacade.AppendText(fullName));
    public ManagedFileInfo CopyTo(string destFileName) => CopyTo(destFileName, false);
    public ManagedFileInfo CopyTo(string destFileName, bool overwrite) => WithProvider(() => { FileFacade.Copy(fullName, destFileName, overwrite); return new ManagedFileInfo(destFileName); });
    public void MoveTo(string destFileName) => MoveTo(destFileName, false);
    public void MoveTo(string destFileName, bool overwrite) => WithProvider(() => { FileFacade.Move(fullName, destFileName, overwrite); fullName = VirtualPath.Display(Provider.Mount, VirtualPath.Resolve(Provider.Mount, destFileName)); });
    public ManagedFileInfo Replace(string destinationFileName, string? destinationBackupFileName) => Replace(destinationFileName, destinationBackupFileName, false);
    public ManagedFileInfo Replace(string destinationFileName, string? destinationBackupFileName, bool ignoreMetadataErrors) =>
        WithProvider(() => { FileFacade.Replace(fullName, destinationFileName, destinationBackupFileName, ignoreMetadataErrors); return new ManagedFileInfo(destinationFileName); });
}

public sealed class ManagedDirectoryInfo : ManagedFileSystemInfo
{
    public ManagedDirectoryInfo(string path) : base(path) { }
    public override bool Exists => Provider.DirectoryExists(fullName);
    public ManagedDirectoryInfo? Parent => WithProvider(() => DirectoryFacade.GetParent(fullName));
    public ManagedDirectoryInfo Root => WithProvider(() => new ManagedDirectoryInfo(Provider.Mount));
    public void Create() => Provider.CreateDirectory(fullName);
    public ManagedDirectoryInfo CreateSubdirectory(string path)
    {
        if (Path.IsPathRooted(path) || path.Split('/').Contains("..")) throw new ArgumentException("Subdirectory must remain within this directory.");
        return WithProvider(() => DirectoryFacade.CreateDirectory(fullName + "/" + path));
    }
    public override void Delete() => Delete(false);
    public void Delete(bool recursive) => WithProvider(() => DirectoryFacade.Delete(fullName, recursive));
    public void MoveTo(string destDirName) => WithProvider(() => { DirectoryFacade.Move(fullName, destDirName); fullName = VirtualPath.Display(Provider.Mount, VirtualPath.Resolve(Provider.Mount, destDirName)); });
    public ManagedFileInfo[] GetFiles() => GetFiles("*");
    public ManagedFileInfo[] GetFiles(string searchPattern) => GetFiles(searchPattern, SearchOption.TopDirectoryOnly);
    public ManagedFileInfo[] GetFiles(string searchPattern, SearchOption searchOption) => WithProvider(() => DirectoryFacade.GetFiles(fullName, searchPattern, searchOption).Select(p => new ManagedFileInfo(p)).ToArray());
    public ManagedFileInfo[] GetFiles(string searchPattern, EnumerationOptions enumerationOptions) => WithProvider(() => DirectoryFacade.GetFiles(fullName, searchPattern, enumerationOptions).Select(p => new ManagedFileInfo(p)).ToArray());
    public ManagedDirectoryInfo[] GetDirectories() => GetDirectories("*");
    public ManagedDirectoryInfo[] GetDirectories(string searchPattern) => GetDirectories(searchPattern, SearchOption.TopDirectoryOnly);
    public ManagedDirectoryInfo[] GetDirectories(string searchPattern, SearchOption searchOption) => WithProvider(() => DirectoryFacade.GetDirectories(fullName, searchPattern, searchOption).Select(p => new ManagedDirectoryInfo(p)).ToArray());
    public ManagedDirectoryInfo[] GetDirectories(string searchPattern, EnumerationOptions enumerationOptions) => WithProvider(() => DirectoryFacade.GetDirectories(fullName, searchPattern, enumerationOptions).Select(p => new ManagedDirectoryInfo(p)).ToArray());
    public IEnumerable<ManagedFileInfo> EnumerateFiles() => GetFiles();
    public IEnumerable<ManagedFileInfo> EnumerateFiles(string searchPattern) => GetFiles(searchPattern);
    public IEnumerable<ManagedFileInfo> EnumerateFiles(string searchPattern, SearchOption searchOption) => GetFiles(searchPattern, searchOption);
    public IEnumerable<ManagedFileInfo> EnumerateFiles(string searchPattern, EnumerationOptions enumerationOptions) => GetFiles(searchPattern, enumerationOptions);
    public IEnumerable<ManagedDirectoryInfo> EnumerateDirectories() => GetDirectories();
    public IEnumerable<ManagedDirectoryInfo> EnumerateDirectories(string searchPattern) => GetDirectories(searchPattern);
    public IEnumerable<ManagedDirectoryInfo> EnumerateDirectories(string searchPattern, SearchOption searchOption) => GetDirectories(searchPattern, searchOption);
    public IEnumerable<ManagedDirectoryInfo> EnumerateDirectories(string searchPattern, EnumerationOptions enumerationOptions) => GetDirectories(searchPattern, enumerationOptions);
    public ManagedFileSystemInfo[] GetFileSystemInfos() => GetFileSystemInfos("*");
    public ManagedFileSystemInfo[] GetFileSystemInfos(string searchPattern) => GetFileSystemInfos(searchPattern, SearchOption.TopDirectoryOnly);
    public ManagedFileSystemInfo[] GetFileSystemInfos(string searchPattern, SearchOption searchOption) => GetFiles(searchPattern, searchOption).Cast<ManagedFileSystemInfo>().Concat(GetDirectories(searchPattern, searchOption)).ToArray();
    public ManagedFileSystemInfo[] GetFileSystemInfos(string searchPattern, EnumerationOptions enumerationOptions) => GetFiles(searchPattern, enumerationOptions).Cast<ManagedFileSystemInfo>().Concat(GetDirectories(searchPattern, enumerationOptions)).ToArray();
    public IEnumerable<ManagedFileSystemInfo> EnumerateFileSystemInfos() => GetFileSystemInfos();
    public IEnumerable<ManagedFileSystemInfo> EnumerateFileSystemInfos(string searchPattern) => GetFileSystemInfos(searchPattern);
    public IEnumerable<ManagedFileSystemInfo> EnumerateFileSystemInfos(string searchPattern, SearchOption searchOption) => GetFileSystemInfos(searchPattern, searchOption);
    public IEnumerable<ManagedFileSystemInfo> EnumerateFileSystemInfos(string searchPattern, EnumerationOptions enumerationOptions) => GetFileSystemInfos(searchPattern, enumerationOptions);
}
