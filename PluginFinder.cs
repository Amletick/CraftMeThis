using System.Collections;
using System.Reflection;
using Dalamud.Plugin;

namespace CraftMeThis;

/// <summary>Finds another plugin's live instance through Dalamud's plugin manager (for plugins without the IPC we need).</summary>
public static class PluginFinder
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal static object? FindPluginInstance(string internalName)
    {
        var asm = typeof(IDalamudPluginInterface).Assembly;
        var pmType = asm.GetType("Dalamud.Plugin.Internal.PluginManager", true)!;
        var service = asm.GetType("Dalamud.Service`1", true)!.MakeGenericType(pmType);
        var pm = service.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        var installed = (IEnumerable)pmType.GetProperty("InstalledPlugins", Any)!.GetValue(pm)!;
        foreach (var lp in installed)
        {
            var lpType = lp.GetType();
            var manifest = lpType.GetProperty("Manifest", Any)?.GetValue(lp);
            var name = manifest?.GetType().GetProperty("InternalName", Any)?.GetValue(manifest) as string;
            if (name != internalName) continue;
            if (lpType.GetProperty("IsLoaded", Any)?.GetValue(lp) is false) return null;
            for (var ty = lpType; ty != null; ty = ty.BaseType)
            {
                var f = ty.GetField("instance", Any);
                if (f != null) return f.GetValue(lp);
            }
        }
        return null;
    }
}
