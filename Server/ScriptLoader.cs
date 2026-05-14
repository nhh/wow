using System.Runtime.Loader;

namespace Server;

public static class ScriptLoader
{
    // Loads the first concrete type implementing T from a pre-compiled DLL.
    // One ALC per unique DLL — caller must deduplicate to avoid redundant loads.
    public static Type LoadType<T>(string dllPath) where T : class
    {
        var ctx = new AssemblyLoadContext(dllPath, isCollectible: true);
        var asm = ctx.LoadFromAssemblyPath(Path.GetFullPath(dllPath));
        return asm.GetTypes()
                  .FirstOrDefault(t => typeof(T).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
               ?? throw new InvalidOperationException(
                      $"No type implementing {typeof(T).Name} found in {dllPath}");
    }

    public static T CreateInstance<T>(Type type) where T : class
        => (T)Activator.CreateInstance(type)!;
}
