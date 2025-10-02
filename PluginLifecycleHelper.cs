using System;
using System.Collections;
using System.IO;
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
            if (!TryGetPluginManagerType(out var pluginManagerType))
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

    public static bool TryLoadPlugin(string pluginName, string pluginDirectory, ConsoleLog consoleLog = null)
    {
        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            return false;
        }

        try
        {
            if (!TryGetPluginManagerType(out var pluginManagerType))
            {
                var message = "Unable to locate GameHelper plugin manager; cannot load plugin automatically.";
                consoleLog?.LogWarning(message);
                PluginLogger.Warn(message);
                return false;
            }

            var pluginsField = pluginManagerType.GetField(
                "Plugins",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            var loadPluginMethod = pluginManagerType.GetMethod(
                "LoadPlugin",
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(DirectoryInfo) },
                modifiers: null);

            var loadMetadataMethod = pluginManagerType.GetMethod(
                "LoadPluginMetadata",
                BindingFlags.Static | BindingFlags.NonPublic);

            var pluginWithNameType = pluginManagerType.Assembly.GetType("GameHelper.Plugin.PluginWithName");

            if (pluginsField == null || loadPluginMethod == null || loadMetadataMethod == null || pluginWithNameType == null)
            {
                var message = "Unable to access GameHelper internals required to load plugins automatically.";
                consoleLog?.LogWarning(message);
                PluginLogger.Warn(message);
                return false;
            }

            var directoryInfo = new DirectoryInfo(pluginDirectory);
            if (!directoryInfo.Exists)
            {
                return false;
            }

            var pluginWithName = loadPluginMethod.Invoke(null, new object[] { directoryInfo });
            if (pluginWithName == null)
            {
                var message = $"GameHelper failed to load plugin from {directoryInfo.FullName}.";
                consoleLog?.LogWarning(message);
                PluginLogger.Warn(message);
                return false;
            }

            var pluginArray = Array.CreateInstance(pluginWithNameType, 1);
            pluginArray.SetValue(pluginWithName, 0);
            loadMetadataMethod.Invoke(null, new object[] { pluginArray });

            var recordedNameProperty = pluginWithNameType.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
            var recordedName = recordedNameProperty?.GetValue(pluginWithName) as string;
            var resolvedPluginName = !string.IsNullOrWhiteSpace(recordedName) ? recordedName : pluginName;

            if (pluginsField.GetValue(null) is not IList plugins || plugins.Count == 0)
            {
                return false;
            }

            object targetContainer = null;
            foreach (var container in plugins)
            {
                var nameProperty = container?.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
                var name = nameProperty?.GetValue(container) as string;
                if (!string.IsNullOrWhiteSpace(name) &&
                    name.Equals(resolvedPluginName, StringComparison.OrdinalIgnoreCase))
                {
                    targetContainer = container;
                    break;
                }
            }

            if (targetContainer == null)
            {
                return false;
            }

            var metadataProperty = targetContainer.GetType().GetProperty("Metadata", BindingFlags.Instance | BindingFlags.Public);
            var metadata = metadataProperty?.GetValue(targetContainer);
            var enableProperty = metadata?.GetType().GetProperty("Enable", BindingFlags.Instance | BindingFlags.Public);
            enableProperty?.SetValue(metadata, true);

            var pluginProperty = targetContainer.GetType().GetProperty("Plugin", BindingFlags.Instance | BindingFlags.Public);
            var pluginInstance = pluginProperty?.GetValue(targetContainer) as IPCore;

            pluginInstance?.OnEnable(IsGameLoaded());

            var successMessage = $"Loaded plugin {resolvedPluginName} after file operations.";
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

    private static bool TryGetPluginManagerType(out Type pluginManagerType)
    {
        pluginManagerType = Type.GetType("GameHelper.Plugin.PManager, GameHelper");
        return pluginManagerType != null;
    }

    private static bool IsGameLoaded()
    {
        try
        {
            var coreType = Type.GetType("GameHelper.Core, GameHelper");
            if (coreType == null)
            {
                return false;
            }

            var processProperty = coreType.GetProperty("Process", BindingFlags.Static | BindingFlags.Public);
            var processInstance = processProperty?.GetValue(null);
            if (processInstance == null)
            {
                return false;
            }

            var addressProperty = processInstance.GetType().GetProperty("Address", BindingFlags.Instance | BindingFlags.Public);
            if (addressProperty?.GetValue(processInstance) is IntPtr address)
            {
                return address != IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            PluginLogger.Warn($"Unable to determine game load state: {ex.Message}");
        }

        return false;
    }
}
