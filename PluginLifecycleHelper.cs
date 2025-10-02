using System;
using System.Linq;
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
            var pluginNames = PManager.PluginNames?.ToList();
            if (pluginNames == null || pluginNames.Count == 0)
            {
                return false;
            }

            var match = pluginNames.FirstOrDefault(name =>
                name.Equals(pluginName, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                return false;
            }

            if (!PManager.UnloadPlugin(match))
            {
                var message = $"Failed to unload plugin {pluginName} before file operations.";
                consoleLog?.LogWarning(message);
                PluginLogger.Info(message);
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
}
