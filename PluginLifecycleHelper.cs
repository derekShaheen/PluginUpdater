using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using GameHelper;
using GameHelper.Plugin;

namespace PluginUpdater;

internal static class PluginLifecycleHelper
{
    public static bool TryUnloadPlugin(string pluginName, ConsoleLog consoleLog = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
        {
            return false;
        }

        try
        {
            var container = PManager.Plugins.FirstOrDefault(
                plugin => plugin != null &&
                          plugin.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase));

            AssemblyLoadContext loadContext = null;

            if (container != null)
            {
                container.Metadata.Enable = false;
                loadContext = AssemblyLoadContext.GetLoadContext(container.Plugin.GetType().Assembly);
            }

            if (!PManager.UnloadPlugin(pluginName))
            {
                return false;
            }

            TryUnloadAssemblyContext(pluginName, loadContext, consoleLog);
            PManager.SavePluginMetadata();
            EnsureAssembliesReleased();

            var successMessage = $"Unloaded plugin {pluginName} before file operations.";
            consoleLog?.LogInfo(successMessage);
            PluginLogger.Info(successMessage);
            return true;
        }
        catch (Exception ex)
        {
            var message = $"Unable to unload plugin {pluginName}: {ex.Message}";
            consoleLog?.LogError(message);
            PluginLogger.Error(message);
            return false;
        }
    }

    private static void TryUnloadAssemblyContext(string pluginName, AssemblyLoadContext context, ConsoleLog consoleLog)
    {
        if (context == null)
        {
            return;
        }

        try
        {
            if (!context.IsCollectible)
            {
                var message = $"Plugin {pluginName} is loaded in a non-collectible context and cannot be fully unloaded.";
                consoleLog?.LogWarning(message);
                PluginLogger.Warn(message);
                return;
            }

            context.Unload();
        }
        catch (Exception ex)
        {
            var message = $"Failed to unload AssemblyLoadContext for {pluginName}: {ex.Message}";
            consoleLog?.LogWarning(message);
            PluginLogger.Warn(message);
        }
    }

    public static bool TryLoadPlugin(string pluginName, string pluginDirectory, ConsoleLog consoleLog = null)
    {
        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            return false;
        }

        try
        {
            var directoryInfo = new DirectoryInfo(pluginDirectory);
            if (!directoryInfo.Exists)
            {
                return false;
            }

            var plugin = PManager.LoadPlugin(directoryInfo);
            if (plugin == null)
            {
                var message = $"GameHelper failed to load plugin from {directoryInfo.FullName}.";
                consoleLog?.LogWarning(message);
                PluginLogger.Warn(message);
                return false;
            }

            PManager.LoadPluginMetadata(new[] { plugin });

            var container = PManager.Plugins.FirstOrDefault(
                candidate => candidate != null &&
                              candidate.Name.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase));

            if (container == null)
            {
                var message = $"Loaded assembly for {plugin.Name} but plugin container was not registered.";
                consoleLog?.LogWarning(message);
                PluginLogger.Warn(message);
                return false;
            }

            container.Metadata.Enable = true;
            PManager.SavePluginMetadata();
            container.Plugin.OnEnable(Core.Process.Address != IntPtr.Zero);

            var successMessage = $"Loaded plugin {plugin.Name} after file operations.";
            consoleLog?.LogInfo(successMessage);
            PluginLogger.Info(successMessage);
            return true;
        }
        catch (Exception ex)
        {
            var message = $"Unable to load plugin {pluginName}: {ex.Message}";
            consoleLog?.LogError(message);
            PluginLogger.Error(message);
            return false;
        }
    }

    public static void EnsureAssembliesReleased()
    {
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        catch (Exception ex)
        {
            PluginLogger.Warn($"Failed to force GC while releasing plugin assemblies: {ex.Message}");
        }
    }
}
