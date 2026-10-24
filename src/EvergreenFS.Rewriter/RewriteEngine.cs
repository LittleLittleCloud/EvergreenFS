using System.Reflection;
using EvergreenFS;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace EvergreenFS.Rewriting;

public static class RewriteEngine
{
    private static readonly Dictionary<string, Type> mappings = new()
    {
        ["System.IO.File"] = typeof(FileFacade),
        ["System.IO.Directory"] = typeof(DirectoryFacade),
        ["System.IO.FileStream"] = typeof(ManagedFileStream),
        ["System.IO.FileInfo"] = typeof(ManagedFileInfo),
        ["System.IO.DirectoryInfo"] = typeof(ManagedDirectoryInfo),
        ["System.IO.FileSystemInfo"] = typeof(ManagedFileSystemInfo),
        ["System.IO.StreamReader"] = typeof(ManagedStreamReader),
        ["System.IO.StreamWriter"] = typeof(ManagedStreamWriter)
    };
    private static bool Trusted(string name) =>
        name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
        name.StartsWith("SQLitePCLRaw.", StringComparison.Ordinal) || name.StartsWith("xunit", StringComparison.Ordinal) ||
        name is "System" or "mscorlib" or "netstandard" or "EvergreenFS" or "EvergreenFS.Rewriter" or "EvergreenFS.CompatibilitySupport" or "testhost" or "Newtonsoft.Json";
    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in Types(type.NestedTypes)) yield return nested;
        }
    }
    private static string Shape(TypeReference type) => type.FullName;
    private static string Signature(MethodReference method) =>
        $"{Shape(method.ReturnType)} {method.Name}({string.Join(",", method.Parameters.Select(p => Shape(p.ParameterType)))})";
    private static IEnumerable<MethodBase> Candidates(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Cast<MethodBase>()
            .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    private static TypeReference Map(ModuleDefinition module, TypeReference type)
    {
        if (type is FunctionPointerType && mappings.Keys.Any(name => type.FullName.Contains(name, StringComparison.Ordinal)))
            throw new NotSupportedException($"EGFS001: IO types in unmanaged/function-pointer signatures are unsupported: {type.FullName}");
        if (type is GenericParameter) return type;
        if (type is GenericInstanceType generic)
        {
            var mapped = new GenericInstanceType(Map(module, generic.ElementType));
            foreach (var argument in generic.GenericArguments) mapped.GenericArguments.Add(Map(module, argument));
            return mapped;
        }
        if (type is ArrayType array) return new ArrayType(Map(module, array.ElementType), array.Rank);
        if (type is ByReferenceType reference) return new ByReferenceType(Map(module, reference.ElementType));
        if (type is PointerType pointer) return new PointerType(Map(module, pointer.ElementType));
        if (type is RequiredModifierType required) return new RequiredModifierType(Map(module, required.ModifierType), Map(module, required.ElementType));
        if (type is OptionalModifierType optional) return new OptionalModifierType(Map(module, optional.ModifierType), Map(module, optional.ElementType));
        return mappings.TryGetValue(type.FullName, out var replacement) ? module.ImportReference(replacement) : type;
    }
    private static MethodReference Remap(ModuleDefinition module, MethodReference method)
    {
        if (method is GenericInstanceMethod instance)
        {
            var result = new GenericInstanceMethod(Remap(module, instance.ElementMethod));
            foreach (var argument in instance.GenericArguments) result.GenericArguments.Add(Map(module, argument));
            return result;
        }
        var mapped = new MethodReference(method.Name, Map(module, method.ReturnType), Map(module, method.DeclaringType))
        {
            HasThis = method.HasThis, ExplicitThis = method.ExplicitThis, CallingConvention = method.CallingConvention
        };
        foreach (var parameter in method.GenericParameters) mapped.GenericParameters.Add(new GenericParameter(parameter.Name, mapped));
        foreach (var parameter in method.Parameters) mapped.Parameters.Add(new ParameterDefinition(Map(module, parameter.ParameterType)));
        return mapped;
    }
    private static MethodReference? Replacement(ModuleDefinition module, MethodReference method)
    {
        if (!mappings.TryGetValue(method.DeclaringType.FullName, out var type)) return null;
        var signature = Signature(Remap(module, method));
        return Candidates(type).Select(module.ImportReference)
            .FirstOrDefault(m => m.HasThis == method.HasThis && Signature(m) == signature);
    }
    public static object[] Inventory()
    {
        using var module = ModuleDefinition.CreateModule("Inventory", ModuleKind.Dll);
        return mappings.Select(pair =>
        {
            var original = typeof(File).Assembly.GetType(pair.Key) ?? Type.GetType(pair.Key)!;
            return (object)new
            {
                type = pair.Key,
                replacement = pair.Value.FullName,
                members = Candidates(original).Select(method =>
                {
                    var reference = module.ImportReference(method);
                    reference.DeclaringType = module.ImportReference(original);
                    return new { signature = reference.FullName, supported = Replacement(module, reference) != null };
                }).OrderBy(m => m.signature).ToArray(),
                fields = original.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                    .Select(field => new { signature = field.ToString(), supported = false }).ToArray()
            };
        }).ToArray();
    }
    public static void Rewrite(string target, bool closure)
    {
        var assemblies = new Dictionary<string, AssemblyDefinition>(StringComparer.Ordinal);
        var staged = new List<(string Path, string Temp, int Count, bool Changed)>();
        try
        {
            void Load(string path)
            {
                if (assemblies.ContainsKey(path)) return;
                var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true });
                assemblies.Add(path, assembly);
                if (Trusted(assembly.Name.Name)) throw new InvalidOperationException("Refusing to rewrite framework/provider internals.");
                if (assembly.Name.HasPublicKey) throw new InvalidOperationException($"Signed assembly {path} is unsupported; rebuild unsigned.");
                if (assembly.Modules.Count != 1) throw new NotSupportedException("Multi-module assemblies are unsupported.");
                if (!closure) return;
                foreach (var dependency in assembly.MainModule.AssemblyReferences.ToArray())
                {
                    if (Trusted(dependency.Name)) continue;
                    var candidate = Path.Combine(Path.GetDirectoryName(path)!, dependency.Name + ".dll");
                    if (!File.Exists(candidate)) throw new InvalidOperationException($"Application dependency '{dependency.Name}' is missing beside '{path}'. Supply the complete intended application closure.");
                    Load(candidate);
                }
            }
            Load(target);
            var errors = new List<string>();
            foreach (var (path, assembly) in assemblies)
            {
                var module = assembly.MainModule;
                var count = 0;
                var changed = module.GetTypeReferences().Any(t => mappings.ContainsKey(t.FullName));
                foreach (var type in Types(module.Types))
                {
                    if (type.BaseType != null)
                    {
                        if (mappings.ContainsKey(type.BaseType.FullName))
                            errors.Add($"error EGFS001: {type.FullName}: deriving from intercepted IO types is unsupported. No output changed.");
                        else type.BaseType = Map(module, type.BaseType);
                    }
                    foreach (var implementation in type.Interfaces) implementation.InterfaceType = Map(module, implementation.InterfaceType);
                    foreach (var parameter in type.GenericParameters)
                        foreach (var constraint in parameter.Constraints) constraint.ConstraintType = Map(module, constraint.ConstraintType);
                    foreach (var field in type.Fields) field.FieldType = Map(module, field.FieldType);
                    foreach (var property in type.Properties)
                    {
                        property.PropertyType = Map(module, property.PropertyType);
                        foreach (var parameter in property.Parameters) parameter.ParameterType = Map(module, parameter.ParameterType);
                    }
                    foreach (var evt in type.Events) evt.EventType = Map(module, evt.EventType);
                    foreach (var method in type.Methods)
                    {
                        method.ReturnType = Map(module, method.ReturnType);
                        foreach (var parameter in method.Parameters) parameter.ParameterType = Map(module, parameter.ParameterType);
                        for (var i = 0; i < method.Overrides.Count; i++) method.Overrides[i] = Remap(module, method.Overrides[i]);
                        foreach (var parameter in method.GenericParameters)
                            foreach (var constraint in parameter.Constraints) constraint.ConstraintType = Map(module, constraint.ConstraintType);
                        if (!method.HasBody) continue;
                        foreach (var variable in method.Body.Variables) variable.VariableType = Map(module, variable.VariableType);
                        foreach (var handler in method.Body.ExceptionHandlers)
                            if (handler.CatchType != null) handler.CatchType = Map(module, handler.CatchType);
                        foreach (var instruction in method.Body.Instructions)
                        {
                            if (instruction.Operand is MethodReference called)
                            {
                                if (mappings.ContainsKey(called.DeclaringType.FullName))
                                {
                                    var replacement = Replacement(module, called);
                                    if (replacement == null)
                                        errors.Add($"error EGFS001: {method.FullName} IL_{instruction.Offset:x4}: unsupported {called.FullName}. Consult --inventory; native handles and unimplemented overloads must be removed. No output changed.");
                                    else { instruction.Operand = replacement; count++; }
                                }
                                else
                                {
                                    var scope = called.DeclaringType.GetElementType().Scope as AssemblyNameReference;
                                    if (scope != null && Trusted(scope.Name) &&
                                        (Shape(called.ReturnType) != Shape(Map(module, called.ReturnType)) ||
                                         called.Parameters.Any(p => Shape(p.ParameterType) != Shape(Map(module, p.ParameterType)))))
                                        errors.Add($"error EGFS001: {method.FullName}: IO type crosses an unrewritten trusted boundary: {called.FullName}. Use a Stream/TextReader/TextWriter boundary or an application dependency. No output changed.");
                                    else instruction.Operand = Remap(module, called);
                                }
                            }
                            else if (instruction.Operand is TypeReference operand) instruction.Operand = Map(module, operand);
                            else if (instruction.Operand is FieldReference field)
                            {
                                if (mappings.ContainsKey(field.DeclaringType.FullName))
                                    errors.Add($"error EGFS001: {method.FullName}: unsupported IO field {field.FullName}. No output changed.");
                                else instruction.Operand = new FieldReference(field.Name, Map(module, field.FieldType), Map(module, field.DeclaringType));
                            }
                            else if (instruction.Operand is CallSite site &&
                                     (Shape(site.ReturnType) != Shape(Map(module, site.ReturnType)) ||
                                      site.Parameters.Any(p => Shape(p.ParameterType) != Shape(Map(module, p.ParameterType)))))
                                errors.Add($"error EGFS001: {method.FullName} IL_{instruction.Offset:x4}: IO types in indirect call signatures are unsupported. No output changed.");
                        }
                    }
                }
                staged.Add((path, path + "." + Guid.NewGuid().ToString("N") + ".evergreen.tmp", count, changed || count != 0));
            }
            if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            // Validate every assembly before replacing any; each individual DLL replacement is atomic.
            foreach (var item in staged)
                if (item.Changed) assemblies[item.Path].Write(item.Temp);
            foreach (var item in staged)
            {
                if (item.Changed) File.Move(item.Temp, item.Path, overwrite: true);
                Console.WriteLine($"EvergreenFS: {Path.GetFileName(item.Path)}: rewritten {item.Count} call site(s).");
            }
        }
        finally
        {
            foreach (var assembly in assemblies.Values) assembly.Dispose();
            foreach (var item in staged) if (File.Exists(item.Temp)) File.Delete(item.Temp);
        }
    }
}
