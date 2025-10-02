using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.Plugin;
using GameHelper.Settings;
using ImGuiNET;
using Newtonsoft.Json;

namespace PluginUpdater;

public sealed class PluginUpdater : PCore<PluginUpdaterSettings>
{
    private const string SettingsFileName = "settings.json";

    public static PluginUpdater Instance { get; private set; }

    private Task _startupTask;
    private CancellationTokenSource _updateLoopCts;
    private Task _updateLoopTask;
    private PluginRenderer _renderer;

    private string PluginDirectory => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory ?? Environment.CurrentDirectory, DllDirectory));

    private string SettingsFilePath => Path.Combine(PluginDirectory, "config", SettingsFileName);

    public override void OnEnable(bool isGameOpened)
    {
        Instance = this;
        LoadSettings();

        var pluginsRoot = State.PluginsDirectory.FullName;
        _renderer = new PluginRenderer(Settings, pluginsRoot);

        _startupTask = Task.Run(_renderer.Startup);

        _updateLoopCts = new CancellationTokenSource();
        _updateLoopTask = Task.Run(() => RunUpdateLoopAsync(_updateLoopCts.Token));
    }

    public override void OnDisable()
    {
        SaveSettings();

        _updateLoopCts?.Cancel();

        try
        {
            _updateLoopTask?.Wait();
        }
        catch (AggregateException ex)
        {
            PluginLogger.Error($"Error while stopping update loop: {ex.Flatten().InnerException}");
        }

        if (_startupTask is { IsFaulted: true } task)
        {
            PluginLogger.Error($"Startup task failed: {task.Exception}");
        }

        _renderer?.Dispose();
        _renderer = null;
        _startupTask = null;
        _updateLoopTask = null;
        _updateLoopCts?.Dispose();
        _updateLoopCts = null;

        Instance = null;
    }

    public override void DrawSettings()
    {
        if (_renderer == null)
        {
            ImGui.Text("Initializing...");
            return;
        }

        if (_startupTask == null)
        {
            ImGui.Text("Preparing plugin updater...");
            return;
        }

        if (!_startupTask.IsCompleted)
        {
            ImGui.Text("Loading plugin information...");
            return;
        }

        if (_startupTask.IsFaulted)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.2f, 0.2f, 1f));
            ImGui.TextWrapped($"Failed to initialize: {_startupTask.Exception?.GetBaseException().Message}");
            ImGui.PopStyleColor();
            return;
        }

        _renderer.DrawSettings();
    }

    public override void DrawUI()
    {
        // No in-game overlay to render.
    }

    public override void SaveSettings()
    {
        SaveSettingsToDisk();
    }

    private async Task RunUpdateLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_startupTask != null)
            {
                await _startupTask.ConfigureAwait(false);
            }

            if (_startupTask is { IsCompletedSuccessfully: true })
            {
                _renderer?.Update();
            }

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_startupTask is { IsCompletedSuccessfully: true })
                {
                    _renderer?.Update();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when disabling the plugin.
        }
        catch (Exception ex)
        {
            PluginLogger.Error($"Update loop failed: {ex}");
        }
    }

    private void LoadSettings()
    {
        try
        {
            var path = SettingsFilePath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonConvert.DeserializeObject<PluginUpdaterSettings>(json);
                if (loaded != null)
                {
                    Settings = loaded;
                    EnsureSettingsInitialized();
                }
            }
        }
        catch (Exception ex)
        {
            PluginLogger.Error($"Failed to load settings: {ex}");
            Settings = new PluginUpdaterSettings();
            EnsureSettingsInitialized();
        }

        EnsureSettingsInitialized();
    }

    private void SaveSettingsToDisk()
    {
        try
        {
            var path = SettingsFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonConvert.SerializeObject(Settings, Formatting.Indented);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            PluginLogger.Error($"Failed to save settings: {ex}");
        }
    }

    private void EnsureSettingsInitialized()
    {
        if (Settings == null)
        {
            Settings = new PluginUpdaterSettings();
        }

        Settings.ReleaseSources = Settings.ReleaseSources != null
            ? new Dictionary<string, string>(Settings.ReleaseSources, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Settings.ReleaseChecksums = Settings.ReleaseChecksums != null
            ? new Dictionary<string, string>(Settings.ReleaseChecksums, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
