using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using GameHelper.Plugin;

namespace PluginUpdater;

public class PluginUpdaterSettings : IPSettings
{
    public bool Enable { get; set; } = true;

    public bool CheckUpdatesOnStartup { get; set; }
        = false;

    public bool AutoCheckUpdates { get; set; }
        = false;

    public bool WrapLogMessages { get; set; }
        = true;

    public int UpdateCheckIntervalMinutes { get; set; }
        = 60;

    public Dictionary<string, string> ReleaseSources { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> ReleaseChecksums { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> ReleaseInstallDirectories { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public List<string> PendingDeletionDirectories { get; set; }
        = new();

    [JsonIgnore]
    public DateTime LastUpdateCheck { get; set; } = DateTime.Now;

    [JsonIgnore]
    public bool HasCheckedUpdates { get; set; }
        = false;

    public bool ShouldPerformPeriodicCheck()
    {
        if (!AutoCheckUpdates)
        {
            return false;
        }

        var timeSinceLastCheck = DateTime.Now - LastUpdateCheck;
        return timeSinceLastCheck.TotalMinutes >= UpdateCheckIntervalMinutes;
    }
}
