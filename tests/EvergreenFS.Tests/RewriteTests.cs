using System.Diagnostics;
using System.Security.Cryptography;
using EvergreenFS;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace EvergreenFS.Tests;

public class RewriteTests
{
    private static readonly string Configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
    private static string Tool => Path.Combine(Workspace.Repository, "src", "EvergreenFS.Rewriter", "bin", Configuration, "net10.0", "EvergreenFS.Rewriter.dll");
    private static (int Code, string Output) Run(params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Workspace.Repository };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("dotnet child process exceeded 120 seconds.");
        }
        return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }

    [Fact]
    public void BuildAutomaticallyRewritesAndOrdinarySystemIoSampleRunsIsolated()
    {
        var build = Run("build", "samples/TransparentApp/TransparentApp.csproj", "--no-restore", "-c", Configuration, "--nologo");
        Assert.True(build.Code == 0, build.Output);
        Assert.Contains("EvergreenFS:", build.Output);
        var sample = Path.Combine(Workspace.Repository, "samples", "TransparentApp", "bin", Configuration, "net10.0", "TransparentApp.dll");
        using (var assembly = AssemblyDefinition.ReadAssembly(sample))
        {
            var calls = assembly.MainModule.Types.SelectMany(t => t.Methods).Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().ToArray();
            Assert.Contains(calls, m => m.DeclaringType.FullName == typeof(FileFacade).FullName);
            Assert.DoesNotContain(calls, m => m.DeclaringType.FullName is "System.IO.File" or "System.IO.Directory");
        }
        var before = SHA256.HashData(File.ReadAllBytes(sample));
        var repeat = Run(Tool, sample);
        Assert.True(repeat.Code == 0, repeat.Output);
        Assert.Contains("rewritten 0 call site(s)", repeat.Output);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(sample)));
        var result = Run(sample);
        Assert.True(result.Code == 0, result.Output);
        Assert.Contains("changed", result.Output);
        Assert.Contains("isolated System.IO", result.Output);
        Assert.Contains("isolated=True", result.Output);
        Assert.False(Directory.Exists("/evergreen-demo"));
    }

    [Theory]
    [InlineData("overload")]
    [InlineData("native-handle")]
    [InlineData("stream")]
    [InlineData("fileinfo")]
    [InlineData("directoryinfo")]
    [InlineData("inherited-info")]
    public void UnsupportedCallsFailActionablyWithoutChangingAssembly(string kind)
    {
        using var workspace = new Workspace();
        var target = Path.Combine(workspace.Root, "Unsupported.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Unsupported", new Version(1, 0)), "Unsupported", ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var type = new TypeDefinition("Tests", "Fixture", TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(type);
            var method = new MethodDefinition("Bad", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            // A valid supported call before the unsupported call proves all-or-nothing validation.
            il.Emit(OpCodes.Ldstr, "/v/supported");
            il.Emit(OpCodes.Call, module.ImportReference(typeof(File).GetMethod(nameof(File.Exists), [typeof(string)])!));
            il.Emit(OpCodes.Pop);
            System.Reflection.MethodBase bad = kind switch
            {
                "overload" => typeof(File).GetMethod(nameof(File.SetCreationTime), [typeof(string), typeof(DateTime)])!,
                "native-handle" => typeof(File).GetMethod(nameof(File.OpenHandle), [typeof(string), typeof(FileMode), typeof(FileAccess), typeof(FileShare), typeof(FileOptions), typeof(long)])!,
                "stream" => typeof(FileStream).GetProperty(nameof(FileStream.SafeFileHandle))!.GetMethod!,
                "fileinfo" => typeof(FileInfo).GetMethod(nameof(FileInfo.ResolveLinkTarget), [typeof(bool)])!,
                "inherited-info" => typeof(FileSystemInfo).GetProperty(nameof(FileSystemInfo.CreationTime))!.GetMethod!,
                _ => typeof(DirectoryInfo).GetMethod(nameof(DirectoryInfo.ResolveLinkTarget), [typeof(bool)])!
            };
            if (kind == "inherited-info") il.Emit(OpCodes.Ldnull);
            else il.Emit(OpCodes.Ldstr, "/v/bad");
            if (kind == "overload") il.Emit(OpCodes.Ldstr, "*");
            if (kind == "stream") il.Emit(OpCodes.Ldc_I4_3);
            il.Emit(bad.IsConstructor ? OpCodes.Newobj : bad.IsStatic ? OpCodes.Call : OpCodes.Callvirt, module.ImportReference(bad));
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
            assembly.Write(target);
        }
        var before = File.ReadAllBytes(target);
        var result = Run(Tool, target);
        Assert.Equal(1, result.Code);
        Assert.Contains("EGFS001", result.Output);
        Assert.Contains("Tests.Fixture::Bad", result.Output);
        Assert.Contains("IL_", result.Output);
        Assert.Contains("No output changed", result.Output);
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.evergreen.tmp"));
    }

    [Fact]
    public void ProviderAssemblyCannotBeRewritten()
    {
        var result = Run(Tool, typeof(IVirtualFileSystem).Assembly.Location);
        Assert.Equal(1, result.Code);
        Assert.Contains("Refusing to rewrite", result.Output);
    }

    [Fact]
    public void EveryDocumentedFacadeOverloadHasAnExactRewritableSystemIoSignature()
    {
        using var workspace = new Workspace();
        var target = Path.Combine(workspace.Root, "AllSupported.dll");
        var supportedCount = 0;
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("AllSupported", new Version(1, 0)), "AllSupported", ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var type = new TypeDefinition("Tests", "Fixture", TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(type);
            var method = new MethodDefinition("Supported", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            foreach (var (facade, native) in new[] { (typeof(FileFacade), typeof(File)), (typeof(DirectoryFacade), typeof(Directory)) })
            foreach (var replacement in facade.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                var parameters = replacement.GetParameters();
                var original = native.GetMethod(replacement.Name, parameters.Select(p => p.ParameterType).ToArray());
                Assert.NotNull(original);
                Assert.Equal(original.ReturnType.FullName?.Replace("System.IO.DirectoryInfo", "EvergreenFS.ManagedDirectoryInfo")
                    .Replace("System.IO.FileStream", "EvergreenFS.ManagedFileStream")
                    .Replace("System.IO.StreamReader", "EvergreenFS.ManagedStreamReader")
                    .Replace("System.IO.StreamWriter", "EvergreenFS.ManagedStreamWriter"), replacement.ReturnType.FullName);
                supportedCount++;
                foreach (var parameter in parameters)
                {
                    if (parameter.ParameterType == typeof(bool)) il.Emit(OpCodes.Ldc_I4_0);
                    else il.Emit(OpCodes.Ldnull);
                }
                il.Emit(OpCodes.Call, module.ImportReference(original));
                if (original.ReturnType != typeof(void)) il.Emit(OpCodes.Pop);
            }
            il.Emit(OpCodes.Ret);
            assembly.Write(target);
        }
        var result = Run(Tool, target);
        Assert.True(result.Code == 0, result.Output);
        Assert.Contains($"rewritten {supportedCount} call site(s)", result.Output);
        using var rewritten = AssemblyDefinition.ReadAssembly(target);
        var calls = rewritten.MainModule.Types.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().ToArray();
        Assert.Equal(supportedCount, calls.Length);
        Assert.All(calls, m => Assert.StartsWith("EvergreenFS.", m.DeclaringType.FullName));
    }

    [Fact]
    public void BuildTargetRejectsUnsupportedApplication()
    {
        using var workspace = new Workspace();
        var project = Path.Combine(workspace.Root, "Rejected.csproj");
        var targets = Path.Combine(Workspace.Repository, "build", "EvergreenFS.targets");
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>
              <Import Project="{targets}" />
            </Project>
            """);
        File.WriteAllText(Path.Combine(workspace.Root, "Program.cs"), """System.IO.File.SetCreationTime("/v/a", System.DateTime.Now);""");
        var result = Run("build", project, "-c", Configuration, "--nologo", "--verbosity", "quiet");
        Assert.NotEqual(0, result.Code);
        Assert.True(result.Output.Contains("EGFS001", StringComparison.Ordinal), result.Output);
        Assert.Contains("System.IO.File::SetCreationTime", result.Output);
        Assert.Contains("No output changed", result.Output);
    }

    [Fact]
    public void CliPersistentWorkflowReadsOldRevisionAndForks()
    {
        using var workspace = new Workspace();
        var cli = Path.Combine(Workspace.Repository, "src", "EvergreenFS.Cli", "bin", Configuration, "net10.0", "EvergreenFS.Cli.dll");
        var database = Path.Combine(workspace.Root, "cli.db");
        var fork = Path.Combine(workspace.Root, "cli-fork.db");
        string Command(string db, params string[] arguments)
        {
            var result = Run([cli, db, .. arguments]);
            Assert.True(result.Code == 0, result.Output);
            return result.Output.Trim();
        }
        Command(database, "mkdir", "/v/notes");
        Command(database, "write", "/v/notes/hello", "first");
        Command(database, "write", "/v/notes/hello", "second");
        Assert.Equal("second", Command(database, "read", "/v/notes/hello"));
        Assert.Equal("first", Command(database, "read", "/v/notes/hello", "2"));
        Assert.Contains("3\twrite\t/v/notes/hello", Command(database, "history"));
        Assert.Contains("/v/notes/hello", Command(database, "tree"));
        Assert.Contains("F /v/notes/hello", Command(database, "list", "/v/notes"));
        Command(database, "fork", "2", fork);
        Assert.Equal("first", Command(fork, "read", "/v/notes/hello"));
        Command(fork, "write", "/v/notes/hello", "fork");
        Assert.Equal("second", Command(database, "read", "/v/notes/hello"));
        var failure = Run(cli, database, "read", "/outside");
        Assert.Equal(1, failure.Code);
        Assert.Contains("outside virtual mount", failure.Output);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("native")]
    public void DependencyInstanceStreamsAsyncAndEnumerationAreRewrittenAndExecuted(string provider)
    {
        using var workspace = new Workspace();
        var app = Path.Combine(Workspace.Repository, "samples", "ManagedApp", "bin", Configuration, "net10.0", "ManagedApp.dll");
        var result = Run(app, provider, Path.Combine(workspace.Root, provider == "sqlite" ? "state.db" : "native"));
        Assert.True(result.Code == 0, result.Output);
        Assert.Contains($"managed-dependency={provider}; assertions=", result.Output);
        Assert.Contains("isolated=True", result.Output);
        var dependency = Path.Combine(Path.GetDirectoryName(app)!, "ManagedDependency.dll");
        using var rewritten = AssemblyDefinition.ReadAssembly(dependency);
        var types = rewritten.MainModule.Types.SelectMany(t => t.NestedTypes.Prepend(t)).ToArray();
        var references = types.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().ToArray();
        Assert.Contains(references, m => m.DeclaringType.FullName == typeof(ManagedFileStream).FullName);
        Assert.Contains(references, m => m.DeclaringType.FullName == typeof(ManagedFileInfo).FullName);
        Assert.DoesNotContain(references, m => m.DeclaringType.FullName is "System.IO.File" or "System.IO.Directory" or "System.IO.FileStream" or "System.IO.FileInfo");
    }

    [Fact]
    public void InventoryIncludesExactUnsupportedNativeHandleSignatures()
    {
        var result = Run(Tool, "--inventory");
        Assert.True(result.Code == 0, result.Output);
        using var json = System.Text.Json.JsonDocument.Parse(result.Output);
        Assert.Equal(8, json.RootElement.GetArrayLength());
        var stream = json.RootElement.EnumerateArray().Single(t => t.GetProperty("type").GetString() == "System.IO.FileStream");
        Assert.Contains(stream.GetProperty("members").EnumerateArray(),
            m => m.GetProperty("signature").GetString()!.Contains("get_SafeFileHandle", StringComparison.Ordinal) && !m.GetProperty("supported").GetBoolean());
        Assert.Contains(stream.GetProperty("members").EnumerateArray(),
            m => m.GetProperty("signature").GetString()!.Contains("Read(System.Span", StringComparison.Ordinal) && m.GetProperty("supported").GetBoolean());
    }

    [Fact]
    public void UnsupportedDependencyAbortsEntireClosureWithoutChangingInputs()
    {
        using var workspace = new Workspace();
        var root = Path.Combine(workspace.Root, "Root.dll");
        var dependency = Path.Combine(workspace.Root, "AppDependency.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Root", new Version(1, 0)), "Root", ModuleKind.Dll))
        {
            assembly.MainModule.AssemblyReferences.Add(new AssemblyNameReference("AppDependency", new Version(1, 0)));
            var type = new TypeDefinition("Tests", "Root", TypeAttributes.Public, assembly.MainModule.TypeSystem.Object);
            type.Fields.Add(new FieldDefinition("Info", FieldAttributes.Public, assembly.MainModule.ImportReference(typeof(FileInfo))));
            assembly.MainModule.Types.Add(type);
            assembly.Write(root);
        }
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("AppDependency", new Version(1, 0)), "Dependency", ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var type = new TypeDefinition("Tests", "Dependency", TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(type);
            var method = new MethodDefinition("Bad", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            type.Methods.Add(method);
            method.Body.GetILProcessor().Emit(OpCodes.Call, module.ImportReference(typeof(FileStream).GetProperty(nameof(FileStream.SafeFileHandle))!.GetMethod!));
            method.Body.GetILProcessor().Emit(OpCodes.Ret);
            assembly.Write(dependency);
        }
        var rootBytes = File.ReadAllBytes(root);
        var dependencyBytes = File.ReadAllBytes(dependency);
        var result = Run(Tool, root, "--closure");
        Assert.Equal(1, result.Code);
        Assert.Contains("EGFS001", result.Output);
        Assert.Contains("SafeFileHandle", result.Output);
        Assert.Equal(rootBytes, File.ReadAllBytes(root));
        Assert.Equal(dependencyBytes, File.ReadAllBytes(dependency));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.evergreen.tmp"));
    }

    [Fact]
    public void SignatureOnlyAssemblyIsRemappedAndRepeatSafe()
    {
        using var workspace = new Workspace();
        var path = Path.Combine(workspace.Root, "Signatures.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Signatures", new Version(1, 0)), "Signatures", ModuleKind.Dll))
        {
            var type = new TypeDefinition("Tests", "Container", TypeAttributes.Public, assembly.MainModule.TypeSystem.Object);
            type.Fields.Add(new FieldDefinition("Files", FieldAttributes.Public, assembly.MainModule.ImportReference(typeof(List<FileInfo[]>))));
            assembly.MainModule.Types.Add(type);
            assembly.Write(path);
        }
        var result = Run(Tool, path);
        Assert.True(result.Code == 0, result.Output);
        using (var assembly = AssemblyDefinition.ReadAssembly(path))
            Assert.Contains("EvergreenFS.ManagedFileInfo[]", assembly.MainModule.Types.Single(t => t.Name == "Container").Fields[0].FieldType.FullName);
        var bytes = File.ReadAllBytes(path);
        var repeat = Run(Tool, path);
        Assert.True(repeat.Code == 0, repeat.Output);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
