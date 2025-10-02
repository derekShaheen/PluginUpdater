using System;
using System.IO;
using System.Linq;
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

            if (container == null)
            {
                return false;
            }

            container.Plugin.SaveSettings();
            container.Plugin.OnDisable();
            PManager.Plugins.Remove(container);

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

            var pluginWithName = PManager.LoadPlugin(directoryInfo);
            if (pluginWithName == null)
            {
                var message = $"GameHelper failed to load plugin from {directoryInfo.FullName}.";
                consoleLog?.LogWarning(message);
                PluginLogger.Warn(message);
                return false;
            }

            PManager.LoadPluginMetadata(new[] { pluginWithName });

            var resolvedName = string.IsNullOrWhiteSpace(pluginWithName.Name)
                ? pluginName
                : pluginWithName.Name;

            var container = PManager.Plugins.FirstOrDefault(
                plugin => plugin != null &&
                          plugin.Name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase));

            if (container == null)
            {
                return false;
            }

            container.Metadata.Enable = true;
            container.Plugin.OnEnable(Core.Process.Address != IntPtr.Zero);

            var successMessage = $"Loaded plugin {container.Name} after file operations.";
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
}
