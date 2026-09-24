using System.Reflection;
using System.Runtime.Loader;
using LeanStudio.Plugins;

namespace LeanStudio.Core.Plugins;

/// <summary>A plugin that loaded, and where from.</summary>
/// <param name="Plugin">The plugin.</param>
/// <param name="Path">Its assembly.</param>
public sealed record LoadedPlugin(ILeanStudioPlugin Plugin, string Path);

/// <summary>
/// Loads compiled plugins (<see cref="ILeanStudioPlugin"/>) from a folder: <c>Name.dll</c> directly in it, or
/// <c>Name/Name.dll</c> with its dependencies beside it. Each assembly gets a load context of its own, which
/// resolves its dependencies from its folder and shares LeanStudio.Plugins with Lean Studio, so the interfaces are
/// the same types on both sides.
/// </summary>
public static class PluginLoader
{
    /// <summary>The plugins folder's name, inside the settings folder.</summary>
    public const string FolderName = "plugins";

    private static readonly string ApiName = typeof(ILeanStudioPlugin).Assembly.GetName().Name!;

    /// <summary>The assemblies in <paramref name="folder"/> that may hold plugins, in name order.</summary>
    public static IReadOnlyList<string> Candidates(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }
        IEnumerable<string> flat = Directory.EnumerateFiles(folder, "*.dll");
        IEnumerable<string> nested = Directory.EnumerateDirectories(folder)
            .Select(d => Path.Combine(d, Path.GetFileName(d) + ".dll"))
            .Where(File.Exists);
        return flat.Concat(nested)
                   .Where(p => !string.Equals(Path.GetFileNameWithoutExtension(p), ApiName, StringComparison.OrdinalIgnoreCase))
                   .Order(StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    /// <summary>
    /// Load every plugin in <paramref name="folder"/> and initialize it with <paramref name="host"/>. A plugin that
    /// fails to load or to initialize is left out, with a line saying why; the others still load.
    /// </summary>
    public static (IReadOnlyList<LoadedPlugin> Plugins, IReadOnlyList<string> Problems) Load(string folder, IPluginHost host)
    {
        var plugins = new List<LoadedPlugin>();
        var problems = new List<string>();
        foreach (string path in Candidates(folder))
        {
            string file = Path.GetFileName(path);
            IReadOnlyList<Type> types;
            try
            {
                Assembly assembly = new PluginContext(path).LoadFromAssemblyPath(Path.GetFullPath(path));
                types = PluginTypes(assembly);
            }
            catch (Exception e) when (e is BadImageFormatException or FileLoadException or FileNotFoundException or IOException)
            {
                problems.Add($"{file}: not a .NET assembly Lean Studio can load ({e.Message})");
                continue;
            }
            if (types.Count == 0)
            {
                problems.Add($"{file}: no public class implements ILeanStudioPlugin (built against LeanStudio.Plugins {typeof(ILeanStudioPlugin).Assembly.GetName().Version?.ToString(2)}?)");
                continue;
            }
            foreach (Type t in types)
            {
                try
                {
                    var plugin = (ILeanStudioPlugin)Activator.CreateInstance(t)!;
                    plugin.Initialize(host);
                    plugins.Add(new LoadedPlugin(plugin, path));
                }
                catch (Exception e)
                {
                    Exception inner = e is TargetInvocationException { InnerException: Exception i } ? i : e;
                    problems.Add($"{file}: {t.FullName} failed to start: {inner.GetType().Name}: {inner.Message}");
                }
            }
        }
        return (plugins, problems);
    }

    private static IReadOnlyList<Type> PluginTypes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.OfType<Type>().ToArray();
        }
        return types.Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ILeanStudioPlugin).IsAssignableFrom(t)
                                && t.GetConstructor(Type.EmptyTypes) is not null)
                    .ToList();
    }

    /// <summary>One plugin's load context: its own dependencies from its folder, LeanStudio.Plugins from the app.</summary>
    private sealed class PluginContext(string mainPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainPath))
    {
        private readonly AssemblyDependencyResolver _resolver = new(Path.GetFullPath(mainPath));

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == ApiName)
            {
                return null; // the app's copy, so ILeanStudioPlugin is one type
            }
            return _resolver.ResolveAssemblyToPath(name) is string path ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is string path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
