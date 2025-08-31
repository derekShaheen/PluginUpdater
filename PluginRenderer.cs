using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ExileCore2;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ImGuiNET;

namespace PluginUpdater
{
    public class PluginRepositoryData
    {
        public List<PluginDescription> PluginDescriptions { get; set; }
    }

    public class PluginDescription
    {
        public string Name { get; set; }
        public string OriginalAuthor { get; set; }
        public List<Fork> Forks { get; set; }
        public string Description { get; set; }
        public string EndorsedAuthor { get; set; }
    }

    public class Fork
    {
        public string Author { get; set; }
        public string Location { get; set; }
        public string Name { get; set; }
        public LatestCommit LatestCommit { get; set; }
        public List<Release> Releases { get; set; }
    }

    public class LatestCommit
    {
        public string Message { get; set; }
        public string Hash { get; set; }
        public string Author { get; set; }
        public string Date { get; set; }
    }

    public class Release
    {
        // the output.json has no data in releases so not sure structure xd
    }

    [Submenu(RenderMethod = nameof(Render))]
    public class PluginRenderer : IDisposable
    {
        private readonly ConsoleLog _consoleLog = new();
        private readonly PluginUpdaterSettings _settings;
        private GitUpdater _updater;

        private bool _isUpdating;
        private readonly Dictionary<string, bool> _updatingPlugins = [];
        private readonly Dictionary<string, bool> _revertingPlugins = [];
        private int _currentProgress;
        private int _totalProgress;
        private bool _isUpdatingAll;
        private string _repoUrl = string.Empty;
        private bool _isCloning;
        private readonly List<PluginDescription> _availablePlugins = [];
        private readonly Dictionary<string, bool> _downloadingPlugins = [];
        private bool _isLoadingRepos;
        private bool _hasLoadedRepos;
        private string _loadError = string.Empty;
        private DateTime _lastPeriodicCheckAttempt = DateTime.MinValue;

        public PluginRenderer(PluginUpdaterSettings settings)
        {
            _settings = settings;
        }

        public void Startup()
        {
            _updater = new GitUpdater(PluginUpdater.Instance.PluginManager);
            _updater.ProgressChanged += (current, total) =>
            {
                _currentProgress = current;
                _totalProgress = total;
            };

            var manuallyDownloadedPlugins = _updater.GetManualPlugins();
            foreach (var plugin in manuallyDownloadedPlugins)
            {
                _consoleLog.LogWarning($"{Path.GetFileName(plugin.Name)} was downloaded manually so cannot be updated via this plugin");
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

        private async Task UpdateGitInfoAsync()
        {
            if (_isUpdating) return;

            try
            {
                PluginUpdater.Instance.RemoveNotification("", "PendingUpdates");

                _isUpdating = true;
                await _updater.UpdateGitInfoAsync();

                var plugins = _updater.GetPluginInfo();
                int updateCount = plugins.Count(p => p.CurrentCommit != p.LatestCommit);
                if (updateCount > 0)
                {
                    _consoleLog.AddNotificationMessage("PendingUpdates", $"There is {updateCount} plugin {(updateCount > 1 ? "updates" : "update")} pending.",
                        ConsoleLog.ColorInfo);
                }
            }
            catch (Exception e)
            {
                var errorMsg = $"Error updating git info: {e}";
                DebugWindow.LogError(errorMsg);
                _consoleLog.LogError($"{errorMsg}");
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
                PluginUpdater.Instance.RemoveNotification("", "PendingUpdates");
            }
            catch (Exception e)
            {
                var errorMsg = $"Error updating plugin {pluginName}: {e.Message}";
                DebugWindow.LogError(errorMsg);
                _consoleLog.LogError($"{errorMsg}");
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
                var outdatedPlugins = plugins.Where(p => p.CurrentCommit != p.LatestCommit).ToList();

                _consoleLog.LogInfo($"Starting update for {outdatedPlugins.Count} plugins...");

                foreach (var plugin in outdatedPlugins)
                {
                    await UpdatePluginAsync(plugin.Name, false);
                }

                PluginUpdater.Instance.RemoveNotification("", "PendingUpdates");
            }
            catch (Exception e)
            {
                DebugWindow.LogError($"Error updating all plugins: {e.Message}");
            }
            finally
            {
                _isUpdatingAll = false;
                _settings.GameController.Memory.Dispose();
            }
        }

        public void Render()
        {
            if (!_settings.Enable.Value)
                return;

            if (ImGui.BeginTabBar("PluginManagerTabs"))
            {
                if (ImGui.BeginTabItem("Manage"))
                {
                    ImGui.Spacing();
                    RenderUpdateButtons();
                    RenderPluginsTable();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Add"))
                {
                    ImGui.Spacing();
                    RenderAddPluginSection();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Browse"))
                {
                    ImGui.Spacing();
                    RenderPluginBrowser();
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
                bool hasUpdates = plugins.Any(p => p.CurrentCommit != p.LatestCommit);

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
                    DebugWindow.LogError($"Error rendering plugin {pluginInfo.Name}: {e.Message}");
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
                DebugWindow.LogError(errorMsg);
                _consoleLog.LogError($"{errorMsg}");
            }
            finally
            {
                _revertingPlugins[pluginName] = false;
            }
        }

        private void RenderActionButtons(PluginInfo pluginInfo)
        {
            if (!string.IsNullOrEmpty(pluginInfo.CurrentCommit))
            {
                bool isUpdating = _updatingPlugins.TryGetValue(pluginInfo.Name, out bool updating) && updating;
                bool isReverting = _revertingPlugins.TryGetValue(pluginInfo.Name, out bool reverting) && reverting;

                if (pluginInfo.CurrentCommit != pluginInfo.LatestCommit)
                {
                    ImGui.BeginDisabled(isReverting || isUpdating);
                    if (pluginInfo.AheadBy == 0 || pluginInfo.BehindBy != 0)
                    {
                        using (ImGuiHelpers.UseStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0, 0.5f, 0, 1.0f)))
                        using (ImGuiHelpers.UseStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0, 0.7f, 0, 1.0f)))
                        using (ImGuiHelpers.UseStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0, 0.3f, 0, 1.0f)))
                            if (ImGui.Button(isUpdating ? $"Updating...##{pluginInfo.Name}" : $"Update##{pluginInfo.Name}"))
                            {
                                _ = UpdatePluginAsync(pluginInfo.Name, false);
                            }

                        ImGui.SameLine();
                    }

                    using (ImGuiHelpers.UseStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.8f, 0.2f, 0.2f, 1.0f)))
                    using (ImGuiHelpers.UseStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(1.0f, 0.3f, 0.3f, 1.0f)))
                    using (ImGuiHelpers.UseStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0.6f, 0.1f, 0.1f, 1.0f)))
                        if (ImGui.Button(isUpdating ? $"Updating...##{pluginInfo.Name}" : $"Force update##{pluginInfo.Name}"))
                        {
                            ImGui.OpenPopup($"Force update plugin {pluginInfo.Name}");
                        }

                    if (ImGui.BeginPopupModal($"Force update plugin {pluginInfo.Name}"))
                    {
                        ImGui.Text($"Are you sure you want to for update the plugin '{pluginInfo.Name}'?\nLocal changes will be lost");
                        ImGui.Spacing();

                        float buttonWidth = 120;
                        float spacing = 20;
                        float totalWidth = buttonWidth * 2 + spacing;
                        ImGui.SetCursorPosX((ImGui.GetWindowSize().X - totalWidth) * 0.5f);

                        using (ImGuiHelpers.UseStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.8f, 0.2f, 0.2f, 1.0f)))
                        using (ImGuiHelpers.UseStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(1.0f, 0.3f, 0.3f, 1.0f)))
                        using (ImGuiHelpers.UseStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0.6f, 0.1f, 0.1f, 1.0f)))

                            if (ImGui.Button("Force update", new System.Numerics.Vector2(buttonWidth, 0)))
                            {
                                ImGui.CloseCurrentPopup();
                                _ = UpdatePluginAsync(pluginInfo.Name, true);
                            }

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

            ImGui.TableNextColumn();
        }

        private void RenderAddPluginSection()
        {
            ImGui.Text("Enter Repository URL:");

            var width = ImGui.GetContentRegionAvail().X - 100;
            ImGui.SetNextItemWidth(width);
            ImGui.InputText("##repoinput", ref _repoUrl, 1024);

            ImGui.SameLine();

            if (_isCloning)
            {
                ImGui.BeginDisabled();
                ImGui.Button("Cloning...");
                ImGui.EndDisabled();
            }
            else if (ImGui.Button("Clone"))
            {
                if (!string.IsNullOrWhiteSpace(_repoUrl))
                {
                    _ = CloneRepositoryAsync(_repoUrl);
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

            ImGui.Spacing();
            ImGui.TextWrapped("Enter the URL of a Git repository to clone. " +
                              "The repository will be cloned into the Plugins\\Source folder.");
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

        private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private async Task LoadRepositoriesAsync()
        {
            if (_isLoadingRepos || _hasLoadedRepos) return;

            try
            {
                _isLoadingRepos = true;
                _loadError = string.Empty;

                using var client = new System.Net.Http.HttpClient();
                var response = await client.GetStringAsync("https://raw.githubusercontent.com/exCore2/PluginBrowserData/refs/heads/data/output.json");

                var repoData = System.Text.Json.JsonSerializer.Deserialize<PluginRepositoryData>(
                    response,
                    SerializerOptions
                );

                _availablePlugins.Clear();
                if (repoData?.PluginDescriptions != null)
                {
                    _availablePlugins.AddRange(repoData.PluginDescriptions);
                }

                _hasLoadedRepos = true;
                _consoleLog.LogSuccess($"Successfully loaded {_availablePlugins.Count} plugins");
            }
            catch (Exception ex)
            {
                _loadError = $"Error loading plugins: {ex.Message}";
                _consoleLog.LogError(_loadError);
            }
            finally
            {
                _isLoadingRepos = false;
            }
        }

        private string _pluginToDelete = null;

        private void RenderPluginBrowser()
        {
            if (!_hasLoadedRepos && !_isLoadingRepos)
            {
                _ = LoadRepositoriesAsync();
            }

            ImGui.TextWrapped("Browse and download available plugins from the ExileCore2 organization.");
            ImGui.Spacing();

            if (_isLoadingRepos)
            {
                ImGui.TextWrapped("Loading available plugins...");
                return;
            }

            if (!string.IsNullOrEmpty(_loadError))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1.0f, 0.2f, 0.2f, 1.0f));
                ImGui.TextWrapped(_loadError);
                ImGui.PopStyleColor();

                if (ImGui.Button("Retry"))
                {
                    _hasLoadedRepos = false;
                    _loadError = string.Empty;
                    _ = LoadRepositoriesAsync();
                }

                return;
            }

            var tableFlags = ImGuiTableFlags.Borders |
                             ImGuiTableFlags.Resizable |
                             ImGuiTableFlags.SizingFixedFit |
                             ImGuiTableFlags.ScrollX |
                             ImGuiTableFlags.ScrollY |
                             ImGuiTableFlags.RowBg |
                             ImGuiTableFlags.Hideable;

            var installedPlugins = _updater.GetPluginInfo();

            if (_availablePlugins.Count == 0)
            {
                ImGui.TextColored(new System.Numerics.Vector4(1.0f, 0.2f, 0.2f, 1.0f), "No plugins found in the repository.");
                return;
            }

            float rowHeight = Math.Max(
                ImGui.GetTextLineHeightWithSpacing(),
                ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.Y * 2
            );

            float totalTableHeight = rowHeight * (_availablePlugins.Count + 1);
            float panelHeight = ImGui.GetContentRegionAvail().Y;
            float tableHeight = Math.Min(totalTableHeight, panelHeight * 0.60f);

            if (!ImGui.BeginTable("##browsertable", 6, tableFlags, new System.Numerics.Vector2(-1, tableHeight)))
                return;

            ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Original Author", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Endorsed Fork", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Last Updated", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            foreach (var plugin in _availablePlugins)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(plugin.Name);

                ImGui.TableNextColumn();
                ImGui.TextWrapped(string.IsNullOrEmpty(plugin.Description) ? "-" : plugin.Description);

                ImGui.TableNextColumn();
                ImGui.Text(string.IsNullOrEmpty(plugin.OriginalAuthor) ? "-" : plugin.OriginalAuthor);

                ImGui.TableNextColumn();
                ImGui.Text(string.IsNullOrEmpty(plugin.EndorsedAuthor) ? "-" : plugin.EndorsedAuthor);

                var endorsedFork = string.IsNullOrWhiteSpace(plugin.EndorsedAuthor)
                    ? plugin.Forks?.FirstOrDefault()
                    : plugin.Forks?.FirstOrDefault(f => f.Author == plugin.EndorsedAuthor);

                ImGui.TableNextColumn();
                var lastUpdated = endorsedFork?.LatestCommit?.Date ?? "-";
                if (lastUpdated != "-")
                {
                    lastUpdated = DateTime.Parse(lastUpdated).ToString("yyyy-MM-dd");
                }

                ImGui.Text(lastUpdated);

                ImGui.TableNextColumn();

                if (endorsedFork == null) continue;

                bool isInstalled = installedPlugins.Any(ip =>
                    ip.Name.Equals(endorsedFork.Name, StringComparison.OrdinalIgnoreCase));

                if (isInstalled)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.8f, 0.2f, 0.2f, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(1.0f, 0.3f, 0.3f, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0.6f, 0.1f, 0.1f, 1.0f));

                    if (ImGui.Button($"Delete##{endorsedFork.Name}"))
                    {
                        _pluginToDelete = endorsedFork.Name;
                        ImGui.OpenPopup("Delete Plugin?");
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Delete this plugin");
                    }

                    ImGui.PopStyleColor(3);
                }
                else
                {
                    bool isDownloading = _downloadingPlugins.TryGetValue(plugin.Name, out bool downloading) && downloading;

                    if (isDownloading)
                    {
                        ImGui.BeginDisabled();
                        ImGui.Button($"Downloading...##{plugin.Name}");
                        ImGui.EndDisabled();
                    }
                    else
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0, 0.5f, 0, 1.0f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0, 0.7f, 0, 1.0f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0, 0.3f, 0, 1.0f));

                        if (ImGui.Button($"Download##{plugin.Name}"))
                        {
                            string cloneUrl = $"https://github.com/{endorsedFork.Location}/{endorsedFork.Name}.git";
                            _ = DownloadPluginAsync(plugin.Name, cloneUrl);
                        }

                        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(endorsedFork.LatestCommit?.Message))
                        {
                            ImGui.SetTooltip($"Latest commit: {endorsedFork.LatestCommit.Message}");
                        }

                        ImGui.PopStyleColor(3);
                    }
                }
            }

            if (ImGui.BeginPopupModal("Delete Plugin?"))
            {
                ImGui.Text($"Are you sure you want to delete the plugin '{_pluginToDelete}'?");
                ImGui.Text("This action cannot be undone!");
                ImGui.Spacing();

                float buttonWidth = 120;
                float spacing = 20;
                float totalWidth = (buttonWidth * 2) + spacing;
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
                        ImGui.CloseCurrentPopup();
                    }
                    catch (Exception ex)
                    {
                        _consoleLog.LogError($"Failed to delete plugin: {ex.Message}");
                        ImGui.CloseCurrentPopup();
                    }
                }

                ImGui.PopStyleColor(3);

                ImGui.SameLine(0, spacing);
                if (ImGui.Button("Cancel", new System.Numerics.Vector2(buttonWidth, 0)))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }

            ImGui.EndTable();
        }

        private async Task DownloadPluginAsync(string pluginName, string cloneUrl)
        {
            if (_downloadingPlugins.TryGetValue(pluginName, out bool isDownloading) && isDownloading)
                return;

            try
            {
                _downloadingPlugins[pluginName] = true;
                _consoleLog.LogInfo($"Downloading plugin: {pluginName}");

                await _updater.CloneRepositoryAsync(cloneUrl);

                _consoleLog.LogSuccess($"Successfully downloaded {pluginName}");
            }
            catch (Exception ex)
            {
                var additionalDetails = ex.Message switch
                {
                    var x when x.Contains("unknown certificate lookup failure", StringComparison.OrdinalIgnoreCase) =>
                        $"This probably means your internet connection to the server the plugin is hosted on ({cloneUrl}) is being disrupted by something",
                    _ => "",
                };
                _consoleLog.LogError($"Error downloading plugin {pluginName}: {ex.Message} {additionalDetails}");
            }
            finally
            {
                _downloadingPlugins[pluginName] = false;
            }
        }


        public void Dispose()
        {
            _updater?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}