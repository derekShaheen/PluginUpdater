using System;
using System.Collections;
using System.Reflection;
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
            var pluginManagerType = Type.GetType("GameHelper.Plugin.PManager, GameHelper");
            if (pluginManagerType == null)
            {
                var message = "Unable to locate GameHelper plugin manager; cannot unload plugin automatically.";
                consoleLog?.LogWarning(message);
                PluginLogger.Warn(message);
                return false;
            }

            var pluginsField = pluginManagerType.GetField(
                "Plugins",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            if (pluginsField?.GetValue(null) is not IList plugins || plugins.Count == 0)
            {
                return false;
            }

            object targetContainer = null;
            foreach (var container in plugins)
            {
                var nameProperty = container?.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
                var name = nameProperty?.GetValue(container) as string;
                if (!string.IsNullOrWhiteSpace(name) && name.Equals(pluginName, StringComparison.OrdinalIgnoreCase))
                {
                    targetContainer = container;
                    break;
                }
            }

            if (targetContainer == null)
            {
                return false;
            }

            var pluginProperty = targetContainer.GetType().GetProperty("Plugin", BindingFlags.Instance | BindingFlags.Public);
            var pluginInstance = pluginProperty?.GetValue(targetContainer) as IPCore;

            pluginInstance?.SaveSettings();
            pluginInstance?.OnDisable();

            plugins.Remove(targetContainer);

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
