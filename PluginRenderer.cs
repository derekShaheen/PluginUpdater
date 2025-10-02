using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using ImGuiNET;

namespace PluginUpdater
{
    public class PluginRenderer : IDisposable
    {
        private readonly ConsoleLog _consoleLog = new();
        private readonly PluginUpdaterSettings _settings;
        private readonly string _pluginRootPath;
        private GitUpdater _updater;

        private bool _isUpdating;
        private readonly Dictionary<string, bool> _updatingPlugins = [];
        private readonly Dictionary<string, bool> _revertingPlugins = [];
        private int _currentProgress;
        private int _totalProgress;
        private bool _isUpdatingAll;
        private string _repoUrl = string.Empty;
        private bool _isCloning;
        private bool _isDownloadingRelease;
        private readonly Dictionary<string, bool> _releaseReinstalling = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _latestReleaseChecksums = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _releaseUpdatesAvailable = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missingChecksumLogged = new(StringComparer.OrdinalIgnoreCase);
        private bool _isCheckingReleaseUpdates;
        private DateTime _lastPeriodicCheckAttempt = DateTime.MinValue;
        private string _pluginToDelete;

        private sealed record ReleaseDownloadInfo(string DownloadUrl, string PluginNameFallback, string NormalizedUrl);

        private sealed record ReleaseUrlParts(string Owner, string Repo, string Tag, bool IsDirectAsset, string AssetName);

        public PluginRenderer(PluginUpdaterSettings settings, string pluginRootPath)
        {
            _settings = settings;
            _pluginRootPath = pluginRootPath;
        }

        public void Startup()
        {
            _updater = new GitUpdater(_pluginRootPath);
            _updater.ProgressChanged += (current, total) =>
            {
                _currentProgress = current;
                _totalProgress = total;
            };

            var manuallyDownloadedPlugins = _updater.GetManualPlugins();
            foreach (var plugin in manuallyDownloadedPlugins)
            {
                _consoleLog.LogInfo($"{plugin.Name} is managed via release archives. Use the Add tab to redownload when new releases are available.");
            }

            if (_settings.CheckUpdatesOnStartup && !_settings.HasCheckedUpdates)
            {
                _settings.HasCheckedUpdates = true;
                _currentProgress = 0;
                _totalProgress = 0;
                _ = UpdateGitInfoAsync();
            }
        }

        public void Update()
        {
            if ((DateTime.Now - _lastPeriodicCheckAttempt).TotalMinutes < 1)
                return;

            _lastPeriodicCheckAttempt = DateTime.Now;

            if (_settings.ShouldPerformPeriodicCheck())
            {
                _settings.LastUpdateCheck = DateTime.Now;
                _ = UpdateGitInfoAsync();
            }
        }

        private async Task CheckReleaseUpdatesAsync()
        {
            if (_isCheckingReleaseUpdates)
            {
                return;
            }

            var releaseSources = PluginUpdater.Instance?.Settings.ReleaseSources;
            if (releaseSources == null || releaseSources.Count == 0)
            {
                _releaseUpdatesAvailable.Clear();
                _latestReleaseChecksums.Clear();
                _missingChecksumLogged.Clear();
                return;
            }

            _isCheckingReleaseUpdates = true;

            try
            {
                var releaseChecksums = PluginUpdater.Instance?.Settings.ReleaseChecksums;

                var trackedPlugins = new HashSet<string>(releaseSources.Keys, StringComparer.OrdinalIgnoreCase);
                _releaseUpdatesAvailable.RemoveWhere(name => !trackedPlugins.Contains(name));

                foreach (var key in _latestReleaseChecksums.Keys.Where(key => !trackedPlugins.Contains(key)).ToList())
                {
                    _latestReleaseChecksums.Remove(key);
                }

                _missingChecksumLogged.RemoveWhere(name => !trackedPlugins.Contains(name));

                int processed = 0;
                _totalProgress = releaseSources.Count;
                _currentProgress = 0;

                foreach (var kvp in releaseSources.ToArray())
                {
                    var pluginName = kvp.Key;
                    var releaseUrl = kvp.Value;

                    if (string.IsNullOrWhiteSpace(releaseUrl))
                    {
                        _currentProgress = ++processed;
                        continue;
                    }

                    if (_releaseReinstalling.TryGetValue(pluginName, out bool reinstalling) && reinstalling)
                    {
                        _currentProgress = ++processed;
                        continue;
                    }

                    try
                    {
                        var releaseInfo = await ResolveReleaseAsync(releaseUrl);
                        var checksum = await DownloadReleaseChecksumAsync(releaseInfo.DownloadUrl);
                        _latestReleaseChecksums[pluginName] = checksum;

                        if (releaseChecksums != null &&
                            releaseChecksums.TryGetValue(pluginName, out var storedChecksum) &&
                            !string.IsNullOrWhiteSpace(storedChecksum))
                        {
                            if (string.Equals(storedChecksum, checksum, StringComparison.OrdinalIgnoreCase))
                            {
                                _releaseUpdatesAvailable.Remove(pluginName);
                            }
                            else
                            {
                                _releaseUpdatesAvailable.Add(pluginName);
                            }
                        }
                        else
                        {
                            if (_missingChecksumLogged.Add(pluginName))
                            {
                                _consoleLog.LogWarning($"No checksum recorded for {pluginName}. Redownload to start release tracking.");
                            }

                            _releaseUpdatesAvailable.Add(pluginName);
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLogger.Warn($"Failed to check release for {pluginName}: {ex.Message}");
                        _consoleLog.LogWarning($"Failed to check release for {pluginName}: {ex.Message}");
                    }
                    finally
                    {
                        _currentProgress = ++processed;
                    }
                }
            }
            finally
            {
                _isCheckingReleaseUpdates = false;
                _currentProgress = 0;
                _totalProgress = 0;
            }
        }

        private async Task UpdateGitInfoAsync()
        {
            if (_isUpdating) return;

            try
            {
                _isUpdating = true;
                await _updater.UpdateGitInfoAsync();
                await CheckReleaseUpdatesAsync();

                var plugins = _updater.GetPluginInfo();
                int updateCount = plugins.Count(p => p.CurrentCommit != p.LatestCommit);
                if (updateCount > 0)
                {
                    var updateLabel = updateCount == 1 ? "update" : "updates";
                    _consoleLog.LogInfo($"There {(updateCount == 1 ? "is" : "are")} {updateCount} plugin {updateLabel} pending.");
                }

                if (_releaseUpdatesAvailable.Count > 0)
                {
                    var releaseLabel = _releaseUpdatesAvailable.Count == 1 ? "release update" : "release updates";
                    _consoleLog.LogInfo($"There {(_releaseUpdatesAvailable.Count == 1 ? "is" : "are")} {_releaseUpdatesAvailable.Count} {releaseLabel} available.");
                }
            }
            catch (Exception e)
            {
                var errorMsg = $"Error updating git info: {e}";
                PluginLogger.Error(errorMsg);
                _consoleLog.LogError(errorMsg);
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private async Task UpdatePluginAsync(string pluginName, bool force)
        {
            if (_updatingPlugins.TryGetValue(pluginName, out bool isUpdating) && isUpdating)
                return;

            try
            {
                _updatingPlugins[pluginName] = true;
                _consoleLog.LogInfo($"Starting update for {pluginName}...");
                if (force)
                {
                    await _updater.ForceUpdatePluginAsync(pluginName);
                }
                else
                {
                    await _updater.UpdatePluginAsync(pluginName);
                }

                var plugin = _updater.GetPluginInfo().FirstOrDefault(p => p.Name == pluginName);
                if (plugin != null && !string.IsNullOrEmpty(plugin.LastMessage))
                {
                    var lines = plugin.LastMessage.Split('\n');
                    foreach (var line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            _consoleLog.LogInfo($"{line.Trim()}");
                        }
                    }
                }

                _consoleLog.LogSuccess($"Successfully updated {pluginName}");
            }
            catch (Exception e)
            {
                var errorMsg = $"Error updating plugin {pluginName}: {e.Message}";
                PluginLogger.Error(errorMsg);
                _consoleLog.LogError(errorMsg);
            }
            finally
            {
                _updatingPlugins[pluginName] = false;
            }
        }

        private async Task UpdateAllPluginsAsync()
        {
            if (_isUpdatingAll) return;

            try
            {
                _isUpdatingAll = true;
                var plugins = _updater.GetPluginInfo();
                var outdatedPlugins = plugins
                    .Where(p => !p.IsManualInstall)
                    .Where(p => p.CurrentCommit != p.LatestCommit)
                    .ToList();

                _consoleLog.LogInfo($"Starting update for {outdatedPlugins.Count} plugins...");

                foreach (var plugin in outdatedPlugins)
                {
                    await UpdatePluginAsync(plugin.Name, false);
                }
            }
            catch (Exception e)
            {
                PluginLogger.Error($"Error updating all plugins: {e.Message}");
            }
            finally
            {
                _isUpdatingAll = false;
            }
        }

        public void DrawSettings()
        {
            if (!_settings.Enable)
                return;

            if (ImGui.BeginTabBar("PluginManagerTabs"))
            {
                if (ImGui.BeginTabItem("Manage"))
                {
                    ImGui.Spacing();
                    RenderUpdateButtons();
                    RenderPluginsTable();
                    RenderDeleteConfirmationModal();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Add"))
                {
                    ImGui.Spacing();
                    RenderAddPluginSection();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Settings"))
                {
                    ImGui.Spacing();
                    RenderSettings();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            _consoleLog.RenderConsoleLog();
        }

        private void RenderSettings()
        {
            bool isEnabled = _settings.Enable;
            if (ImGui.Checkbox("Enable plugin updater", ref isEnabled))
            {
                _settings.Enable = isEnabled;
            }

            ImGui.Spacing();

            bool checkStartup = _settings.CheckUpdatesOnStartup;
            if (ImGui.Checkbox("Check for updates on startup", ref checkStartup))
            {
                _settings.CheckUpdatesOnStartup = checkStartup;
                _settings.HasCheckedUpdates = false;
            }

            bool wrapLogMessages = _settings.WrapLogMessages;
            if (ImGui.Checkbox("Wrap log messages", ref wrapLogMessages))
            {
                _settings.WrapLogMessages = wrapLogMessages;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Automatically check for plugin updates when the game starts");
            }

            ImGui.Spacing();

            bool autoCheck = _settings.AutoCheckUpdates;
            if (ImGui.Checkbox("Automatically check for updates periodically", ref autoCheck))
            {
                _settings.AutoCheckUpdates = autoCheck;
            }

            if (autoCheck)
            {
                ImGui.Spacing();

                int interval = _settings.UpdateCheckIntervalMinutes;

                int[] intervals = [15, 30, 60, 120, 180, 360, 720, 1440];
                int currentIndex = Array.BinarySearch(intervals, interval);
                if (currentIndex < 0) currentIndex = 0;

                string[] intervalStrings = intervals.Select(i =>
                    i < 60 ? $"{i} minutes" :
                    i == 60 ? "1 hour" :
                    i < 1440 ? $"{i / 60} hours" :
                    "24 hours").ToArray();

                ImGui.SetNextItemWidth(120);
                if (ImGui.BeginCombo("Check every", intervalStrings[currentIndex], ImGuiComboFlags.PopupAlignLeft))
                {
                    for (int i = 0; i < intervalStrings.Length; i++)
                    {
                        bool isSelected = i == currentIndex;
                        if (ImGui.Selectable(intervalStrings[i], isSelected))
                        {
                            _settings.UpdateCheckIntervalMinutes = intervals[i];
                        }

                        if (isSelected)
                            ImGui.SetItemDefaultFocus();
                    }

                    ImGui.EndCombo();
                }
            }

        }

        private string _pluginNameFilter = "";

        private void RenderUpdateButtons()
        {
            if (_isUpdating)
            {
                ImGui.BeginDisabled();
                string progressText = _totalProgress > 0
                    ? $"Checking For Updates... ({_currentProgress}/{_totalProgress})"
                    : "Checking For Updates...";
                ImGui.Button(progressText);

                ImGui.EndDisabled();
            }
            else
            {
                if (ImGui.Button("Check For Updates"))
                {
                    _currentProgress = 0;
                    _totalProgress = 0;
                    _ = UpdateGitInfoAsync();
                }

                ImGui.SameLine();

                if (ImGui.Button("Refresh (local)"))
                {
                    _updater.UpdateLocal();
                }

                var plugins = _updater.GetPluginInfo();
                var gitPlugins = plugins.Where(p => !p.IsManualInstall).ToList();
                bool hasUpdates = gitPlugins.Any(p => p.CurrentCommit != p.LatestCommit);

                if (hasUpdates)
                {
                    ImGui.SameLine();

                    if (_isUpdatingAll)
                    {
                        ImGui.BeginDisabled();
                        ImGui.Button("Updating All...");
                        ImGui.EndDisabled();
                    }
                    else
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0, 0.5f, 0, 1.0f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0, 0.7f, 0, 1.0f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0, 0.3f, 0, 1.0f));

                        if (ImGui.Button("Update All"))
                        {
                            _ = UpdateAllPluginsAsync();
                        }

                        ImGui.PopStyleColor(3);
                    }
                }
            }

            ImGui.SameLine();

            ImGui.InputTextWithHint("##filter", "Filter", ref _pluginNameFilter, 200);

            if (_releaseUpdatesAvailable.Count > 0)
            {
                ImGui.SameLine();
                var releaseText = _releaseUpdatesAvailable.Count == 1
                    ? "1 release update available"
                    : $"{_releaseUpdatesAvailable.Count} release updates available";
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.8f, 0.2f, 1f), releaseText);
            }

            if(_isUpdating && _totalProgress > 0)
            {
                float progress = (float)_currentProgress / _totalProgress;
                ImGui.ProgressBar(progress, new System.Numerics.Vector2(-1, 2));
            }
        }


        private void RenderPluginsTable()
        {
            var tableFlags = ImGuiTableFlags.Borders |
                             ImGuiTableFlags.Resizable |
                             ImGuiTableFlags.SizingFixedFit |
                             ImGuiTableFlags.ScrollY |
                             ImGuiTableFlags.ScrollX |
                             ImGuiTableFlags.RowBg;

            var plugins = _updater.GetPluginInfo();

            float rowHeight = Math.Max(
                ImGui.GetTextLineHeightWithSpacing(),
                ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.Y * 2
            );

            float totalTableHeight = rowHeight * (plugins.Count + 1);

            float panelHeight = ImGui.GetContentRegionAvail().Y;

            float tableHeight = Math.Min(totalTableHeight, panelHeight * 0.60f);

            if (!ImGui.BeginTable("##table1", 4, tableFlags, new System.Numerics.Vector2(-1, tableHeight)))
                return;

            SetupTableColumns();
            ImGui.TableHeadersRow();
            ImGui.TableNextColumn();

            RenderTableRows();

            ImGui.EndTable();
        }

        private static void SetupTableColumns()
        {
            ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Branch", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Commit", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed);
        }

        private void RenderTableRows()
        {
            var plugins = _updater.GetPluginInfo();

            foreach (var pluginInfo in plugins
                         .Where(x => string.IsNullOrEmpty(_pluginNameFilter) || x.Name.Contains(_pluginNameFilter, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => x.Name))
            {
                try
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    RenderPluginName(pluginInfo);
                    RenderBranchSelector(pluginInfo);
                    RenderCommitInfo(pluginInfo);
                    RenderActionButtons(pluginInfo);
                }
                catch (Exception e)
                {
                    PluginLogger.Error($"Error rendering plugin {pluginInfo.Name}: {e.Message}");
                }
            }
        }

        private static void RenderPluginName(PluginInfo pluginInfo)
        {
            if (!string.IsNullOrEmpty(pluginInfo.Error))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1, 0, 0, 1));
                ImGui.Text(pluginInfo.Name);
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.Text(pluginInfo.Name);
            }

            ImGui.TableNextColumn();
        }

        private void RenderBranchSelector(PluginInfo pluginInfo)
        {
            if (string.IsNullOrEmpty(pluginInfo.CurrentBranch))
            {
                ImGui.TableNextColumn();
                return;
            }

            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 5);
            if (ImGui.BeginCombo($"##branch_{pluginInfo.Name}", pluginInfo.CurrentBranch))
            {
                foreach (var branch in pluginInfo.AvailableBranches)
                {
                    bool isSelected = branch == pluginInfo.CurrentBranch;
                    if (ImGui.Selectable(branch, isSelected))
                    {
                        if (branch != pluginInfo.CurrentBranch)
                        {
                            _consoleLog.LogInfo($"Switching {pluginInfo.Name} to branch {branch}...");
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _updater.ChangeBranchAsync(pluginInfo, branch);
                                    _consoleLog.LogSuccess($"Successfully switched {pluginInfo.Name} to branch {branch}");
                                }
                                catch (Exception ex)
                                {
                                    _consoleLog.LogError($"Switch failed: {ex}");
                                }
                            });
                        }
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.TableNextColumn();
        }

        private static void RenderCommitInfo(PluginInfo pluginInfo)
        {
            if (!string.IsNullOrEmpty(pluginInfo.Error))
            {
                ImGui.TextColored(new System.Numerics.Vector4(1, 0, 0, 1), "Update failed");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(pluginInfo.Error);
                }

                ImGui.TableNextColumn();
                return;
            }

            if (pluginInfo.IsManualInstall)
            {
                var releaseSources = PluginUpdater.Instance?.Settings.ReleaseSources;
                var releaseChecksums = PluginUpdater.Instance?.Settings.ReleaseChecksums;
                if (releaseSources != null && releaseSources.TryGetValue(pluginInfo.Name, out var source))
                {
                    ImGui.TextWrapped($"Installed from release: {source}");
                }
                else
                {
                    ImGui.TextWrapped("Installed from release archive. Use the Add tab to redownload when updates are available.");
                }

                if (_releaseUpdatesAvailable.Contains(pluginInfo.Name))
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.6f, 0f, 1f));
                    ImGui.TextWrapped("New release available. Use Redownload to install the latest files.");
                    ImGui.PopStyleColor();

                    if (releaseChecksums != null && releaseChecksums.TryGetValue(pluginInfo.Name, out var storedChecksum) && !string.IsNullOrWhiteSpace(storedChecksum))
                    {
                        ImGui.Text($"Installed checksum: {ShortenHash(storedChecksum)}");
                    }

                    if (_latestReleaseChecksums.TryGetValue(pluginInfo.Name, out var latestChecksum))
                    {
                        ImGui.Text($"Latest checksum: {ShortenHash(latestChecksum)}");
                    }
                }
                else if (releaseChecksums != null && releaseChecksums.TryGetValue(pluginInfo.Name, out var installedChecksum) && !string.IsNullOrWhiteSpace(installedChecksum))
                {
                    ImGui.Text($"Installed checksum: {ShortenHash(installedChecksum)}");
                }

                ImGui.TableNextColumn();
                return;
            }

            string text;
            if (string.IsNullOrEmpty(pluginInfo.LatestCommit))
            {
                text = "No commit information";
            }
            else if (pluginInfo.CurrentCommit == pluginInfo.LatestCommit)
            {
                text = $"Currently on latest commit ({pluginInfo.CurrentCommit})";
            }
            else
            {
                text = $"{pluginInfo.CurrentCommit} -> {pluginInfo.LatestCommit} '{pluginInfo.LatestCommitMessage}' ({pluginInfo.BehindAhead})";
            }

            if (pluginInfo.UncommittedChangeCount > 0)
            {
                text += $" ({pluginInfo.UncommittedChangeCount} uncommitted changes)";
            }

            ImGui.TextWrapped(text);
            ImGui.TableNextColumn();
        }

        private async Task RevertPluginAsync(string pluginName)
        {
            if (_revertingPlugins.TryGetValue(pluginName, out bool isReverting) && isReverting)
                return;

            try
            {
                _revertingPlugins[pluginName] = true;
                _consoleLog.LogInfo($"Starting revert for {pluginName}...");
                await _updater.RevertPluginAsync(pluginName);
                var plugin = _updater.GetPluginInfo().FirstOrDefault(p => p.Name == pluginName);
                if (plugin != null && !string.IsNullOrEmpty(plugin.LastMessage))
                {
                    var lines = plugin.LastMessage.Split('\n');
                    foreach (var line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            _consoleLog.LogInfo($"{line.Trim()}");
                        }
                    }
                }

                _consoleLog.LogSuccess($"Successfully reverted {pluginName}");
            }
            catch (Exception e)
            {
                var errorMsg = $"Error reverting plugin {pluginName}: {e.Message}";
                PluginLogger.Error(errorMsg);
                _consoleLog.LogError(errorMsg);
            }
            finally
            {
                _revertingPlugins[pluginName] = false;
            }
        }

        private void RenderActionButtons(PluginInfo pluginInfo)
        {
            if (pluginInfo.IsManualInstall)
            {
                var releaseSources = PluginUpdater.Instance?.Settings.ReleaseSources;
                var hasSource = releaseSources != null && releaseSources.TryGetValue(pluginInfo.Name, out var releaseUrl) && !string.IsNullOrWhiteSpace(releaseUrl);
                bool isReinstalling = _releaseReinstalling.TryGetValue(pluginInfo.Name, out bool reinstalling) && reinstalling;

                if (hasSource)
                {
                    if (isReinstalling)
                    {
                        ImGui.BeginDisabled();
                        ImGui.Button($"Redownloading...##{pluginInfo.Name}");
                        ImGui.EndDisabled();
                    }
                    else
                    {
                        bool hasUpdate = _releaseUpdatesAvailable.Contains(pluginInfo.Name);

                        if (hasUpdate)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0f, 0.5f, 0f, 1.0f));
                            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0f, 0.7f, 0f, 1.0f));
                            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0f, 0.3f, 0f, 1.0f));
                        }

                        var buttonLabel = hasUpdate ? $"Update##{pluginInfo.Name}" : $"Redownload##{pluginInfo.Name}";

                        if (ImGui.Button(buttonLabel))
                        {
                            _ = DownloadReleaseAsync(releaseUrl, pluginInfo.Name);
                        }

                        if (hasUpdate)
                        {
                            ImGui.PopStyleColor(3);
                        }
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Download the latest release archive from the saved URL and replace the plugin files.");
                    }

                    ImGui.SameLine();
                }
                else
                {
                    ImGui.TextDisabled("No release URL saved");
                    ImGui.SameLine();
                }
            }
            else if (!string.IsNullOrEmpty(pluginInfo.CurrentCommit))
            {
                bool isUpdating = _updatingPlugins.TryGetValue(pluginInfo.Name, out bool updating) && updating;
                bool isReverting = _revertingPlugins.TryGetValue(pluginInfo.Name, out bool reverting) && reverting;

                if (pluginInfo.CurrentCommit != pluginInfo.LatestCommit)
                {
                    ImGui.BeginDisabled(isReverting || isUpdating);
                    if (pluginInfo.AheadBy == 0 || pluginInfo.BehindBy != 0)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0, 0.5f, 0, 1.0f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0, 0.7f, 0, 1.0f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0, 0.3f, 0, 1.0f));
                        if (ImGui.Button(isUpdating ? $"Updating...##{pluginInfo.Name}" : $"Update##{pluginInfo.Name}"))
                        {
                            _ = UpdatePluginAsync(pluginInfo.Name, false);
                        }

                        ImGui.PopStyleColor(3);
                        ImGui.SameLine();
                    }

                    ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.8f, 0.2f, 0.2f, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(1.0f, 0.3f, 0.3f, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0.6f, 0.1f, 0.1f, 1.0f));
                    if (ImGui.Button(isUpdating ? $"Updating...##{pluginInfo.Name}" : $"Force update##{pluginInfo.Name}"))
                    {
                        ImGui.OpenPopup($"Force update plugin {pluginInfo.Name}");
                    }
                    ImGui.PopStyleColor(3);

                    if (ImGui.BeginPopupModal($"Force update plugin {pluginInfo.Name}"))
                    {
                        ImGui.Text($"Are you sure you want to for update the plugin '{pluginInfo.Name}'?\nLocal changes will be lost");
                        ImGui.Spacing();

                        float buttonWidth = 120;
                        float spacing = 20;
                        float totalWidth = buttonWidth * 2 + spacing;
                        ImGui.SetCursorPosX((ImGui.GetWindowSize().X - totalWidth) * 0.5f);

                        ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.8f, 0.2f, 0.2f, 1.0f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(1.0f, 0.3f, 0.3f, 1.0f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0.6f, 0.1f, 0.1f, 1.0f));

                        if (ImGui.Button("Force update", new System.Numerics.Vector2(buttonWidth, 0)))
                        {
                            ImGui.CloseCurrentPopup();
                            _ = UpdatePluginAsync(pluginInfo.Name, true);
                        }

                        ImGui.PopStyleColor(3);

                        ImGui.SameLine(0, spacing);
                        if (ImGui.Button("Cancel", new System.Numerics.Vector2(buttonWidth, 0)))
                        {
                            ImGui.CloseCurrentPopup();
                        }

                        ImGui.EndPopup();
                    }

                    ImGui.EndDisabled();
                    ImGui.SameLine();
                }


                ImGui.BeginDisabled(isReverting || isUpdating);
                if (ImGui.Button(isReverting ? $"Reverting...##{pluginInfo.Name}" : $"Revert##{pluginInfo.Name}"))
                {
                    _ = RevertPluginAsync(pluginInfo.Name);
                }

                ImGui.EndDisabled();

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"Revert to the previous commit {pluginInfo.PreviousCommit ?? "HEAD~1"}");
                }

                ImGui.SameLine();
            }

            if (ImGui.Button($"Open folder##{pluginInfo.Name}"))
            {
                Process.Start("explorer.exe", pluginInfo.Path);
            }

            ImGui.SameLine();
            if (ImGui.Button($"Delete##{pluginInfo.Name}"))
            {
                _pluginToDelete = pluginInfo.Name;
                ImGui.OpenPopup("Delete Plugin?");
            }

            ImGui.TableNextColumn();
        }

        private void RenderAddPluginSection()
        {
            ImGui.Text("Enter Plugin Source URL:");

            var width = Math.Max(150f, ImGui.GetContentRegionAvail().X - 220f);
            ImGui.SetNextItemWidth(width);
            ImGui.InputText("##repoinput", ref _repoUrl, 1024);

            ImGui.SameLine();

            if (_isCloning)
            {
                ImGui.BeginDisabled();
                ImGui.Button("Cloning Git...");
                ImGui.EndDisabled();
            }
            else if (ImGui.Button("Clone Git"))
            {
                if (!string.IsNullOrWhiteSpace(_repoUrl))
                {
                    if (TryParseGitHubReleaseUrl(_repoUrl, out _))
                    {
                        _consoleLog.LogWarning("This appears to be a GitHub release link. Use the Download Release button instead.");
                    }
                    else
                    {
                        _ = CloneRepositoryAsync(_repoUrl);
                    }
                }
                else
                {
                    _consoleLog.LogWarning("Please enter a repository URL");
                }
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Clone the repository into the plugins folder");
            }

            ImGui.SameLine();

            if (_isDownloadingRelease)
            {
                ImGui.BeginDisabled();
                ImGui.Button("Downloading release...");
                ImGui.EndDisabled();
            }
            else if (ImGui.Button("Download Release"))
            {
                if (!string.IsNullOrWhiteSpace(_repoUrl))
                {
                    _ = DownloadReleaseAsync(_repoUrl);
                }
                else
                {
                    _consoleLog.LogWarning("Please enter a release URL");
                }
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Download the latest release archive from GitHub and extract it into the plugins folder");
            }

            ImGui.Spacing();
            ImGui.TextWrapped("Provide a Git repository URL or a GitHub release link (including direct .zip asset URLs). " +
                              "Release archives are automatically extracted into the Plugins folder.");
        }

        private async Task CloneRepositoryAsync(string repoUrl)
        {
            if (_isCloning) return;

            try
            {
                _isCloning = true;
                _consoleLog.LogInfo($"Cloning repository: {repoUrl}");

                await _updater.CloneRepositoryAsync(repoUrl);

                _consoleLog.LogSuccess($"Successfully cloned repository");
                _repoUrl = string.Empty;
            }
            catch (Exception ex)
            {
                _consoleLog.LogError($"Error cloning repository: {ex.Message}");
            }
            finally
            {
                _isCloning = false;
            }
        }

        private async Task DownloadReleaseAsync(string releaseUrl, string existingPluginName = null)
        {
            if (string.IsNullOrWhiteSpace(releaseUrl))
            {
                _consoleLog.LogWarning("Please enter a release URL");
                return;
            }

            bool isReinstall = !string.IsNullOrWhiteSpace(existingPluginName);

            if (isReinstall)
            {
                if (_releaseReinstalling.TryGetValue(existingPluginName, out bool running) && running)
                {
                    return;
                }

                _releaseReinstalling[existingPluginName] = true;
            }
            else
            {
                if (_isDownloadingRelease)
                {
                    return;
                }

                _isDownloadingRelease = true;
            }

            string tempZip = Path.Combine(Path.GetTempPath(), $"PluginUpdater_{Guid.NewGuid():N}.zip");
            string tempDirectory = Path.Combine(Path.GetTempPath(), $"PluginUpdater_{Guid.NewGuid():N}");

            try
            {
                Directory.CreateDirectory(tempDirectory);

                var releaseInfo = await ResolveReleaseAsync(releaseUrl);

                _consoleLog.LogInfo($"Downloading release from {releaseInfo.NormalizedUrl}...");

                await DownloadFileAsync(releaseInfo.DownloadUrl, tempZip);
                var releaseChecksum = ComputeFileChecksum(tempZip);

                ZipFile.ExtractToDirectory(tempZip, tempDirectory, true);

                var (sourceDirectory, pluginName) = DetermineExtractionRoot(tempDirectory, existingPluginName, releaseInfo.PluginNameFallback);
                pluginName = SanitizePluginName(pluginName);

                var targetDirectory = Path.Combine(_pluginRootPath, pluginName);

                if (Directory.Exists(targetDirectory))
                {
                    PluginLifecycleHelper.TryUnloadPlugin(pluginName, _consoleLog);
                    DeleteDirectory(targetDirectory);
                    _consoleLog.LogInfo($"Replacing existing plugin {pluginName} with the downloaded release");
                }
                else
                {
                    _consoleLog.LogInfo($"Installing plugin {pluginName} from release archive");
                }

                Directory.CreateDirectory(targetDirectory);
                CopyDirectoryContents(sourceDirectory, targetDirectory);

                _consoleLog.LogSuccess($"Installed release for {pluginName}");

                if (!isReinstall)
                {
                    _repoUrl = string.Empty;
                }

                var releaseSources = PluginUpdater.Instance?.Settings.ReleaseSources;
                var releaseChecksums = PluginUpdater.Instance?.Settings.ReleaseChecksums;
                bool settingsChanged = false;

                if (releaseSources != null)
                {
                    releaseSources[pluginName] = releaseInfo.NormalizedUrl;
                    settingsChanged = true;
                }

                if (releaseChecksums != null)
                {
                    releaseChecksums[pluginName] = releaseChecksum;
                    settingsChanged = true;
                }

                if (settingsChanged)
                {
                    PluginUpdater.Instance?.SaveSettings();
                }

                _latestReleaseChecksums[pluginName] = releaseChecksum;
                _releaseUpdatesAvailable.Remove(pluginName);
                _missingChecksumLogged.Remove(pluginName);

                _updater.UpdateLocal();
            }
            catch (Exception ex)
            {
                _consoleLog.LogError($"Error downloading release: {ex.Message}");
            }
            finally
            {
                if (File.Exists(tempZip))
                {
                    try
                    {
                        File.Delete(tempZip);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                if (Directory.Exists(tempDirectory))
                {
                    try
                    {
                        Directory.Delete(tempDirectory, true);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                if (isReinstall)
                {
                    _releaseReinstalling.Remove(existingPluginName);
                }
                else
                {
                    _isDownloadingRelease = false;
                }
            }
        }

        private static async Task<string> DownloadReleaseChecksumAsync(string downloadUrl)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"PluginUpdater_checksum_{Guid.NewGuid():N}.zip");

            try
            {
                await DownloadFileAsync(downloadUrl, tempFile);
                return ComputeFileChecksum(tempFile);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try
                    {
                        File.Delete(tempFile);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }

        private static async Task DownloadFileAsync(string url, string destinationPath)
        {
            using var client = CreateHttpClient();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(destinationPath);
            await stream.CopyToAsync(fileStream);
        }

        private static string ComputeFileChecksum(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }

        private static string ShortenHash(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                return "unknown";
            }

            return hash.Length > 8 ? hash[..8] : hash;
        }

        private async Task<ReleaseDownloadInfo> ResolveReleaseAsync(string releaseUrl)
        {
            if (!TryParseGitHubReleaseUrl(releaseUrl, out var parts))
            {
                throw new InvalidOperationException("Only GitHub release URLs are supported");
            }

            if (parts.IsDirectAsset)
            {
                var fallback = Path.GetFileNameWithoutExtension(parts.AssetName);
                var normalized = string.IsNullOrWhiteSpace(parts.Tag)
                    ? $"https://github.com/{parts.Owner}/{parts.Repo}/releases"
                    : $"https://github.com/{parts.Owner}/{parts.Repo}/releases/tag/{parts.Tag}";

                return new ReleaseDownloadInfo(releaseUrl, fallback, normalized);
            }

            var apiSuffix = !string.IsNullOrWhiteSpace(parts.Tag)
                ? $"tags/{parts.Tag}"
                : "latest";

            var apiUrl = $"https://api.github.com/repos/{parts.Owner}/{parts.Repo}/releases/{apiSuffix}";

            using var client = CreateHttpClient();
            using var response = await client.GetAsync(apiUrl);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);

            var root = document.RootElement;

            string tagName = null;
            if (root.TryGetProperty("tag_name", out var tagProperty))
            {
                tagName = tagProperty.GetString();
            }

            string downloadUrl = null;
            string assetName = null;

            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var candidateUrl = asset.TryGetProperty("browser_download_url", out var urlProperty)
                        ? urlProperty.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(candidateUrl) ||
                        !candidateUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    downloadUrl = candidateUrl;
                    assetName = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                throw new InvalidOperationException("No .zip assets found for the specified release");
            }

            var fallbackName = !string.IsNullOrWhiteSpace(assetName)
                ? Path.GetFileNameWithoutExtension(assetName)
                : parts.Repo;

            var normalizedUrl = !string.IsNullOrWhiteSpace(tagName)
                ? $"https://github.com/{parts.Owner}/{parts.Repo}/releases/tag/{tagName}"
                : $"https://github.com/{parts.Owner}/{parts.Repo}/releases";

            return new ReleaseDownloadInfo(downloadUrl, fallbackName, normalizedUrl);
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PluginUpdater", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            return client;
        }

        private static bool TryParseGitHubReleaseUrl(string releaseUrl, out ReleaseUrlParts parts)
        {
            parts = null;

            if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3)
            {
                return false;
            }

            if (!segments[2].Equals("releases", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var owner = segments[0];
            var repo = segments[1];
            string tag = null;
            bool isDirectAsset = false;
            string assetName = null;

            if (segments.Length >= 4)
            {
                var segment = segments[3];
                if (segment.Equals("tag", StringComparison.OrdinalIgnoreCase) && segments.Length >= 5)
                {
                    tag = segments[4];
                }
                else if (segment.Equals("download", StringComparison.OrdinalIgnoreCase) && segments.Length >= 6)
                {
                    isDirectAsset = true;
                    tag = segments[4];
                    assetName = segments[5];
                }
            }

            parts = new ReleaseUrlParts(owner, repo, tag, isDirectAsset, assetName);
            return true;
        }

        private static (string SourceDirectory, string PluginName) DetermineExtractionRoot(string extractedRoot, string existingPluginName, string fallbackName)
        {
            if (!string.IsNullOrWhiteSpace(existingPluginName))
            {
                return (extractedRoot, existingPluginName);
            }

            var directories = Directory.GetDirectories(extractedRoot);
            var files = Directory.GetFiles(extractedRoot);

            if (directories.Length == 1 && files.Length == 0)
            {
                var directory = directories[0];
                var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return (directory, string.IsNullOrWhiteSpace(name) ? fallbackName : name);
            }

            return (extractedRoot, fallbackName);
        }

        private static void CopyDirectoryContents(string sourceDirectory, string targetDirectory)
        {
            foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDirectory, directory);
                Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
            }

            foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDirectory, file);
                var destination = Path.Combine(targetDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, true);
            }
        }

        private static void DeleteDirectory(string directory)
        {
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            foreach (var dir in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(dir, FileAttributes.Normal);
            }

            Directory.Delete(directory, true);
        }

        private static string SanitizePluginName(string pluginName)
        {
            if (string.IsNullOrWhiteSpace(pluginName))
            {
                return $"Plugin_{Guid.NewGuid():N}";
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(pluginName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());

            return string.IsNullOrWhiteSpace(sanitized) ? $"Plugin_{Guid.NewGuid():N}" : sanitized;
        }

        private void RenderDeleteConfirmationModal()
        {
            if (!ImGui.BeginPopupModal("Delete Plugin?", ImGuiWindowFlags.AlwaysAutoResize))
            {
                return;
            }

            ImGui.Text($"Are you sure you want to delete the plugin '{_pluginToDelete}'?");
            ImGui.Text("This action cannot be undone!");
            ImGui.Spacing();

            float buttonWidth = 120f;
            float spacing = 20f;
            float totalWidth = (buttonWidth * 2f) + spacing;
            ImGui.SetCursorPosX((ImGui.GetWindowSize().X - totalWidth) * 0.5f);

            ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.8f, 0.2f, 0.2f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(1.0f, 0.3f, 0.3f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0.6f, 0.1f, 0.1f, 1.0f));

            if (ImGui.Button("Delete", new System.Numerics.Vector2(buttonWidth, 0)))
            {
                try
                {
                    _updater.DeletePlugin(_pluginToDelete);
                    _consoleLog.LogSuccess($"Successfully deleted plugin: {_pluginToDelete}");

                    var releaseSources = PluginUpdater.Instance?.Settings.ReleaseSources;
                    var releaseChecksums = PluginUpdater.Instance?.Settings.ReleaseChecksums;
                    bool settingsChanged = false;

                    if (releaseSources != null && releaseSources.Remove(_pluginToDelete))
                    {
                        settingsChanged = true;
                    }

                    if (releaseChecksums != null && releaseChecksums.Remove(_pluginToDelete))
                    {
                        settingsChanged = true;
                    }

                    if (settingsChanged)
                    {
                        PluginUpdater.Instance?.SaveSettings();
                    }

                    _releaseUpdatesAvailable.Remove(_pluginToDelete);
                    _latestReleaseChecksums.Remove(_pluginToDelete);
                    _missingChecksumLogged.Remove(_pluginToDelete);

                    _updater.UpdateLocal();
                }
                catch (Exception ex)
                {
                    _consoleLog.LogError($"Failed to delete plugin: {ex.Message}");
                }
                finally
                {
                    _pluginToDelete = null;
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.PopStyleColor(3);

            ImGui.SameLine(0, spacing);
            if (ImGui.Button("Cancel", new System.Numerics.Vector2(buttonWidth, 0)))
            {
                _pluginToDelete = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }




        public void Dispose()
        {
            _updater?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}