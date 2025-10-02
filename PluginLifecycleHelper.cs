using System;
using System.Collections.Generic;
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
            if (!PManager.UnloadPlugin(pluginName))
            {
                return false;
            }

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

            var knownNames = new HashSet<string>(PManager.Plugins.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

            if (!PManager.LoadPlugin(pluginName))
            {
                var message = $"GameHelper failed to load plugin from {directoryInfo.FullName}.";
                consoleLog?.LogWarning(message);
                PluginLogger.Warn(message);
                return false;
            }

            var container = PManager.Plugins.FirstOrDefault(
                plugin => plugin != null && !knownNames.Contains(plugin.Name));

            container ??= PManager.Plugins.FirstOrDefault(
                plugin => plugin != null &&
                          plugin.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase));

            if (container != null)
            {
                container.Metadata.Enable = true;
                PManager.SavePluginMetadata();
            }

            var successMessage = $"Loaded plugin {pluginName} after file operations.";
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
