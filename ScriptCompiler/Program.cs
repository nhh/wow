using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: ScriptCompiler <world.db> <output-dir>");
    return 1;
}

string dbPath  = Path.GetFullPath(args[0]);
string outDir  = Path.GetFullPath(args[1]);

if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"Database not found: {dbPath}");
    return 1;
}

var refs = BuildReferences();
int compiled = 0, failed = 0;

using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();

using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT name, CAST(data AS TEXT) FROM sqlar WHERE name LIKE '%.cs'";
using var reader = cmd.ExecuteReader();

while (reader.Read())
{
    string name   = reader.GetString(0);
    string source = reader.GetString(1);

    string dllName = Path.ChangeExtension(name, ".dll");
    string dllPath = Path.Combine(outDir, dllName);
    Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);

    Console.Write($"  {name} -> {dllName} ... ");

    var tree        = CSharpSyntaxTree.ParseText(source);
    var compilation = CSharpCompilation.Create(
        assemblyName: Path.GetFileNameWithoutExtension(name),
        syntaxTrees:  [tree],
        references:   refs,
        options:      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    using var ms     = new MemoryStream();
    var       result = compilation.Emit(ms);

    if (!result.Success)
    {
        Console.WriteLine("FAILED");
        foreach (var d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            Console.Error.WriteLine($"    {d}");
        failed++;
        continue;
    }

    File.WriteAllBytes(dllPath, ms.ToArray());
    Console.WriteLine("OK");
    compiled++;
}

Console.WriteLine($"\nCompiled {compiled} script(s). Failed: {failed}.");
return failed > 0 ? 1 : 0;

static List<MetadataReference> BuildReferences()
{
    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
    if (tpa is not null)
        foreach (var p in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(p)) paths.Add(p);

    var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
    if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
        foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
            try { AssemblyName.GetAssemblyName(dll); paths.Add(dll); }
            catch (BadImageFormatException) { }

    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location))
            paths.Add(asm.Location);

    return paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
}
