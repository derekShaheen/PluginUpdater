using System;
using ExileCore2;
using ExileCore2.Shared.Attributes;
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

    [IgnoreMenu]
    public bool CheckUpdatesOnStartup { get; set; }

    [IgnoreMenu]
    public bool AutoCheckUpdates { get; set; }

    [IgnoreMenu]
    public bool WrapLogMessages { get; set; } = true;

    [IgnoreMenu]
    public int UpdateCheckIntervalMinutes { get; set; } = 60;

    [JsonIgnore]
    [IgnoreMenu]
    public DateTime LastUpdateCheck { get; set; } = DateTime.Now;

    [JsonIgnore]
    [IgnoreMenu]
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

