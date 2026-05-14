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
        var refs = new List<MetadataReference>();
        // Core runtime assemblies
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
            refs.Add(MetadataReference.CreateFromFile(asm.Location));
        }
        return refs;
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
