using System;

namespace PluginUpdater;

internal static class PluginLogger
{
    public static void Info(string message)
    {
        Console.WriteLine($"[PluginUpdater][INFO] {message}");
    }

    public static void Warn(string message)
    {
        Console.WriteLine($"[PluginUpdater][WARN] {message}");
    }

    public static void Error(string message)
    {
        Console.WriteLine($"[PluginUpdater][ERROR] {message}");
    }
}
