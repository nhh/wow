using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Server;

public static class ScriptCompiler
{
    private static readonly IReadOnlyList<MetadataReference> References = BuildReferences();

    private static List<MetadataReference> BuildReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Framework-dependent: host populates TRUSTED_PLATFORM_ASSEMBLIES with all ref assemblies.
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (tpa is not null)
            foreach (var p in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                if (File.Exists(p)) paths.Add(p);

        // Self-contained (non-single-file): typeof(object).Assembly.Location points to the
        // runtime dir on disk, which contains System.Runtime.dll and all BCL facades.
        // Skip native DLLs (clrgc.dll, clretwrc.dll, etc.) — they have no managed metadata.
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
            foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
                try { AssemblyName.GetAssemblyName(dll); paths.Add(dll); }
                catch (BadImageFormatException) { }

        // Project assemblies (Shared.dll etc.) not in TPA or runtimeDir.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location))
                paths.Add(asm.Location);

        return paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
    }

    public static T[] CompileAll<T>(IReadOnlyList<(string source, string typeName)> scripts) where T : class
    {
        var results = new T[scripts.Count];
        Parallel.For(0, scripts.Count, i =>
            results[i] = Compile<T>(scripts[i].source, scripts[i].typeName));
        return results;
    }

    public static T Compile<T>(string source, string typeName) where T : class
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName: $"Script_{Guid.NewGuid():N}",
            syntaxTrees:  [tree],
            references:   References,
            options:      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            throw new InvalidOperationException($"Script compile error:\n{errors}");
        }

        ms.Seek(0, SeekOrigin.Begin);
        var ctx      = new AssemblyLoadContext($"Script_{typeName}", isCollectible: true);
        var assembly = ctx.LoadFromStream(ms);
        var type     = assembly.GetType(typeName)
                       ?? throw new InvalidOperationException($"Type '{typeName}' not found in script.");

        return (T)Activator.CreateInstance(type)!;
    }
}
