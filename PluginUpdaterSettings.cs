using System;
using ExileCore2;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using Newtonsoft.Json;

namespace PluginUpdater;

public class PluginUpdaterSettings : ISettings
{
    public PluginUpdaterSettings()
    {
        PluginConfig = new PluginRenderer(this);
    }

    public ToggleNode Enable { get; set; } = new ToggleNode(true);
    public bool CheckUpdatesOnStartup { get; set; }
    public bool AutoCheckUpdates { get; set; }
    public bool WrapLogMessages { get; set; } = true;

    public int UpdateCheckIntervalMinutes { get; set; } = 60;

    [JsonIgnore]
    public DateTime LastUpdateCheck { get; set; } = DateTime.Now;
    [JsonIgnore]
    public bool HasCheckedUpdates { get; set; } = false;
    [JsonIgnore]
    public PluginRenderer PluginConfig { get; set; }
    [JsonIgnore]
    public GameController GameController { get; set; } // this is very lazy

    public bool ShouldPerformPeriodicCheck()
    {
        if (!AutoCheckUpdates)
            return false;

        var timeSinceLastCheck = DateTime.Now - LastUpdateCheck;
        return timeSinceLastCheck.TotalMinutes >= UpdateCheckIntervalMinutes;
    }
}

