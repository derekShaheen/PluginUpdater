using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace PluginUpdater
{
    public class PluginInfo
    {
        public required string Name { get; set; }
        public required string Path { get; set; }
        public string CurrentCommit { get; set; } = "";
        public string LatestCommit { get; set; } = "";
        public string BehindAhead { get; set; } = "";
        public int BehindBy { get; set; } = 0;
        public int AheadBy { get; set; } = 0;
        public string LastMessage { get; set; } = "";
        public string LatestCommitMessage { get; set; }
        public string PreviousCommit { get; set; }
        public int UncommittedChangeCount { get; set; }
        public List<string> AvailableBranches { get; set; } = new();
        public string CurrentBranch { get; set; }
        public string Error { get; set; }
        public bool IsManualInstall { get; set; }
    }

    public class GitUpdater : IDisposable
    {
        private readonly string _pluginFolder;
        private readonly ConcurrentDictionary<string, PluginInfo> _pluginInfo = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _updateCts;
        private Task _updateTask;

        public event Action<int, int> ProgressChanged;
        private void ReportProgress(int current, int total) => ProgressChanged?.Invoke(current, total);

        private IEnumerable<PluginInfo> GetGitPlugins() =>
            Directory.GetDirectories(_pluginFolder)
                .Select(resolvedPath =>
                {
                    var gitPath = Path.Join(resolvedPath, ".git");
                    if (Directory.Exists(gitPath) || File.Exists(gitPath))
                    {
                        return CreatePluginInfo(Path.GetFileName(resolvedPath), resolvedPath);
                    }

                    return null;
                })
                .Where(p => p != null);

        public IEnumerable<PluginInfo> GetManualPlugins() =>
            Directory.GetDirectories(_pluginFolder)
                .Select(resolvedPath =>
                {
                    var gitPath = Path.Join(resolvedPath, ".git");
                    if (!Directory.Exists(gitPath) && !File.Exists(gitPath))
                    {
                        var pluginInfo = CreatePluginInfo(Path.GetFileName(resolvedPath), resolvedPath);
                        pluginInfo.IsManualInstall = true;
                        return pluginInfo;
                    }

                    return null;
                })
                .Where(p => p != null);

        public GitUpdater(string pluginFolder)
        {
            _pluginFolder = pluginFolder;
            UpdateLocal();
        }

        private static PluginInfo CreatePluginInfo(string name, string folder)
        {
            return new PluginInfo { Name = name, Path = folder };
        }

        public void UpdateLocal()
        {
            _pluginInfo.Clear();

            var folders = GetGitPlugins().ToList();

            foreach (var pluginInfo in folders)
            {
                try
                {
                    using var repo = new Repository(pluginInfo.Path);
                    SetPluginInfo(pluginInfo, repo);
                    _pluginInfo[pluginInfo.Name] = pluginInfo;
                }
                catch (Exception e)
                {
                    PluginLogger.Error($"Error processing {pluginInfo.Path}: {e}");
                }
            }

            AddManualPlugins();
        }

        public async Task UpdateGitInfoAsync()
        {
            if (_updateTask != null && !_updateTask.IsCompleted)
            {
                var result = MessageBox.Show(
                    "Update in progress, do you want to restart?",
                    "Update in progress",
                    MessageBoxButtons.YesNo);

                if (result == DialogResult.No)
                    return;

                _updateCts?.Cancel();

                try
                {
                    await Task.WhenAny(_updateTask, Task.Delay(5000));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    PluginLogger.Error($"Error during task cancellation: {ex.Message}");
                }
            }

            _updateCts = new CancellationTokenSource();

            try
            {
                _updateTask = UpdateGitInfoInternalAsync(_updateCts.Token);
                await _updateTask;
            }
            catch (OperationCanceledException)
            {
                PluginLogger.Info("Git update operation was cancelled");
            }
            catch (Exception ex)
            {
                PluginLogger.Error($"Error during git update: {ex.Message}");
                throw;
            }
        }

        private void SetPluginInfo(PluginInfo plugin, Repository repository)
        {
            SetBranchInfo(plugin, repository);
            
            plugin.CurrentCommit = repository.Head.Tip.Sha[..7];
            plugin.UncommittedChangeCount = repository.Diff.Compare<TreeChanges>().Count;
            var trackingBranch = repository.Head.TrackedBranch;
            if (trackingBranch != null)
            {
                plugin.LatestCommit = trackingBranch.Tip.Sha[..7];
                plugin.LatestCommitMessage = trackingBranch.Tip.MessageShort;

                if (plugin.CurrentCommit != plugin.LatestCommit)
                {
                    var ahead = repository.Head.TrackingDetails.AheadBy ?? 0;
                    var behind = repository.Head.TrackingDetails.BehindBy ?? 0;
                    plugin.BehindAhead = $"{behind} behind, {ahead} ahead";
                    plugin.BehindBy = behind;
                    plugin.AheadBy = ahead;
                }
                else
                {
                    plugin.BehindBy = 0;
                    plugin.AheadBy = 0;
                    plugin.BehindAhead = "";
                }
            }
            else
            {
                plugin.LatestCommit = plugin.CurrentCommit;
                plugin.LatestCommitMessage = null;
                plugin.BehindAhead = "";
            }

            var previousCommit = repository.Head.Tip.Parents.FirstOrDefault();
            plugin.PreviousCommit = previousCommit != null 
                ? $"{previousCommit.Sha[..7]} {previousCommit.MessageShort}" 
                : null;
        }

        private void SetBranchInfo(PluginInfo plugin, Repository repository)
        {
            // Get all local branches
            var localBranches = repository.Branches
                .Where(b => !b.IsRemote)
                .Where(b => b.FriendlyName != "HEAD")
                .ToList();

            var trackedBranches = localBranches
                .Select(x => GetTrackedBranchSafe(repository, x))
                .Where(x => x != null)
                .ToHashSet();

            // Get all remote branches
            var remoteBranches = repository.Branches
                .Where(b => b.IsRemote)
                .Where(b => !b.FriendlyName.EndsWith("/HEAD"))
                .Except(trackedBranches)
                .Distinct();

            // Combine and deduplicate branches
            plugin.AvailableBranches = localBranches
                .Union(remoteBranches)
                .Select(b => b.FriendlyName)
                .Order()
                .ToList();
            
            plugin.CurrentBranch = repository.Head.FriendlyName;
        }

        private static Branch GetTrackedBranchSafe(Repository repository, Branch x)
        {
            return x.RemoteName == null ||
                   x.RemoteName.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ||
                   repository.Network.Remotes[x.RemoteName] == null
                ? null
                : x.TrackedBranch;
        }

        private async Task UpdateGitInfoInternalAsync(CancellationToken cancellationToken)
        {
            _pluginInfo.Clear();
            var folders = GetGitPlugins().ToList();
            var totalFolders = folders.Count;
            var currentFolder = 0;

            ReportProgress(0, totalFolders);

            foreach (var pluginInfo in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await Task.Run(() =>
                    {
                        using var repo = new Repository(pluginInfo.Path);
                        SetPluginInfo(pluginInfo, repo);
                        _pluginInfo[pluginInfo.Name] = pluginInfo;

                        var fetchOptions = new FetchOptions
                        {
                            CredentialsProvider = _credentialsHandler,
                        };

                        var branchName = repo.Head.FriendlyName;
                        var trackingBranch = repo.Head.TrackedBranch;

                        var remote = GetHeadRemote(repo, false);
                        var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);

                        Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, null);

                        if (trackingBranch == null)
                        {
                            var remoteBranch = repo.Branches[$"origin/{branchName}"];
                            if (remoteBranch != null)
                            {
                                repo.Branches.Update(repo.Head,
                                    b => b.TrackedBranch = remoteBranch.CanonicalName);
                            }
                        }

                        SetPluginInfo(pluginInfo, repo);
                    }, cancellationToken);

                    _pluginInfo[pluginInfo.Name] = pluginInfo;
                }
                catch (Exception e)
                {
                    PluginLogger.Error($"Error processing {pluginInfo.Path}: {e}");
                    pluginInfo.Error = e.Message;
                    if (_pluginInfo.ContainsKey(pluginInfo.Name))
                    {
                        _pluginInfo[pluginInfo.Name] = pluginInfo;
                    }
                    else
                    {
                        using var repo = new Repository(pluginInfo.Path);
                        SetPluginInfo(pluginInfo, repo);
                        _pluginInfo.TryAdd(pluginInfo.Name, pluginInfo);
                    }
                }
                finally
                {
                    currentFolder++;
                    ReportProgress(currentFolder, totalFolders);
                }
            }

            AddManualPlugins();
        }

        public async Task RevertPluginAsync(string pluginName)
        {
            var folder = GetGitPlugins().FirstOrDefault(f => f.Name.Equals(pluginName, StringComparison.InvariantCultureIgnoreCase));
            if (folder == null) return;

            var plugin = _pluginInfo.GetValueOrDefault(pluginName);
            if (plugin == null || plugin.IsManualInstall) return;

            PluginLifecycleHelper.TryUnloadPlugin(pluginName);

            await Task.Run(() =>
            {
                try
                {
                    using var repo = new Repository(folder.Path);
                    var parent = repo.Head.Tip.Parents.FirstOrDefault() ?? throw new Exception("Cannot revert: no parent commit found");
                    repo.Reset(ResetMode.Hard, parent);
                    plugin.LastMessage = $"Reset to parent commit {parent.Sha[..7]}";
                    SetPluginInfo(plugin, repo);

                    PluginLogger.Info($"{folder.Path} reverted to {plugin.CurrentCommit}");
                    PluginLogger.Info(plugin.LastMessage);
                }
                catch (Exception ex)
                {
                    var errorMsg = $"Error reverting plugin {pluginName}: {ex}";
                    PluginLogger.Error(errorMsg);
                    throw;
                }
            });
        }

        public async Task UpdatePluginAsync(string pluginName)
        {
            var folder = GetGitPlugins().FirstOrDefault(f => f.Name.Equals(pluginName, StringComparison.InvariantCultureIgnoreCase));
            if (folder == null) return;

            var plugin = _pluginInfo.GetValueOrDefault(pluginName);
            if (plugin == null || plugin.IsManualInstall) return;

            PluginLifecycleHelper.TryUnloadPlugin(pluginName);

            await Task.Run(() =>
            {
                try
                {
                    using var repo = new Repository(folder.Path);

                    var remote = GetHeadRemote(repo);

                    var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);
                    Commands.Fetch(repo, remote.Name, refSpecs, new FetchOptions
                    {
                        CredentialsProvider = _credentialsHandler,
                    }, null);

                    var mergeResult = Commands.Pull(
                        repo,
                        new Signature("Plugin Updater", "updater@local", DateTimeOffset.Now),
                        new PullOptions());

                    plugin.LastMessage = mergeResult.Status == MergeStatus.UpToDate
                        ? "Already up to date"
                        : $"Updated to {mergeResult.Commit.Id.Sha[..7]}";

                    SetPluginInfo(plugin, repo);

                    PluginLogger.Info($"{folder.Path} updated to {plugin.CurrentCommit}");
                    PluginLogger.Info(plugin.LastMessage);
                }
                catch (Exception ex)
                {
                    PluginLogger.Error($"Error updating plugin {pluginName}: {ex}");
                    throw;
                }
            });
        }

        public async Task ForceUpdatePluginAsync(string pluginName)
        {
            var folder = GetGitPlugins().FirstOrDefault(f => f.Name.Equals(pluginName, StringComparison.InvariantCultureIgnoreCase));
            if (folder == null) return;

            var plugin = _pluginInfo.GetValueOrDefault(pluginName);
            if (plugin == null || plugin.IsManualInstall) return;

            PluginLifecycleHelper.TryUnloadPlugin(pluginName);

            await Task.Run(() =>
            {
                try
                {
                    using var repo = new Repository(folder.Path);

                    var remote = GetHeadRemote(repo);

                    var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);
                    Commands.Fetch(repo, remote.Name, refSpecs, new FetchOptions
                    {
                        CredentialsProvider = _credentialsHandler,
                    }, null);

                    var trackedBranch = repo.Head.TrackedBranch;
                    if (repo.Head.Tip != trackedBranch.Tip)
                    {
                        repo.Reset(ResetMode.Hard, trackedBranch.Tip);
                        plugin.LastMessage = $"Force updated to {trackedBranch.Tip.Id.Sha[..7]}";
                    }
                    else
                    {
                        plugin.LastMessage = "Already up to date";
                    }

                    SetPluginInfo(plugin, repo);

                    PluginLogger.Info($"{folder.Path} updated to {plugin.CurrentCommit}");
                    PluginLogger.Info(plugin.LastMessage);
                }
                catch (Exception ex)
                {
                    PluginLogger.Error($"Error updating plugin {pluginName}: {ex}");
                    throw;
                }
            });
        }

        private static Remote GetHeadRemote(Repository repo, bool throwOnNoTracking = true)
        {
            var headTrackedBranch = repo.Head.TrackedBranch;
            if (throwOnNoTracking && headTrackedBranch is null)
            {
                throw new Exception("No tracking branch found");
            }

            return (headTrackedBranch, repo.Head) switch
            {
                ({ RemoteName: { } remoteName }, _) => repo.Network.Remotes[remoteName],
                (_, { RemoteName: { } remoteName }) => repo.Network.Remotes[remoteName],
                _ => repo.Network.Remotes["updater_remote"] ?? repo.Network.Remotes["origin"] ?? repo.Network.Remotes.Single(),
            };
        }

        private static string ExtractRepoNameAndBranch(string repoUrl, out string branch)
        {
            branch = null;

            var treeIndex = repoUrl.IndexOf("/tree/", StringComparison.OrdinalIgnoreCase);
            if (treeIndex != -1)
            {
                branch = repoUrl[(treeIndex + 6)..];
                repoUrl = repoUrl[..treeIndex];
            }

            var repoName = Path.GetFileNameWithoutExtension(repoUrl.TrimEnd('/'));
            if (string.IsNullOrEmpty(repoName))
                repoName = repoUrl.Split('/').Last().Replace(".git", "");

            return repoName;
        }

        public async Task CloneRepositoryAsync(string repoUrl)
        {
            var repoName = ExtractRepoNameAndBranch(repoUrl, out var branch);
            var targetPath = Path.Combine(_pluginFolder, repoName);

            if (Directory.Exists(targetPath))
                throw new InvalidOperationException($"A plugin with the name {repoName} already exists");

            await Task.Run(() =>
            {
                try
                {
                    var fetchOptions = new FetchOptions
                    {
                        CredentialsProvider = _credentialsHandler,
                    };

                    var cloneOptions = new CloneOptions
                    {
                        IsBare = false,
                        Checkout = true,
                    };
                    cloneOptions.FetchOptions.CredentialsProvider = fetchOptions.CredentialsProvider;

                    var cleanUrl = repoUrl;
                    var treeIndex = cleanUrl.IndexOf("/tree/", StringComparison.OrdinalIgnoreCase);
                    if (treeIndex != -1)
                    {
                        cleanUrl = cleanUrl[..treeIndex];
                        if (!cleanUrl.EndsWith(".git"))
                            cleanUrl += ".git";
                    }

                    Repository.Clone(cleanUrl, targetPath, cloneOptions);

                    if (!string.IsNullOrEmpty(branch))
                    {
                        using var branchRepo = new Repository(targetPath);
                        var targetBranch = branchRepo.Branches[$"origin/{branch}"] ?? throw new Exception($"Branch '{branch}' not found in repository");
                        var localBranch = branchRepo.CreateBranch(branch, targetBranch.Tip);
                        branchRepo.Branches.Update(localBranch,
                            b => b.TrackedBranch = targetBranch.CanonicalName);

                        Commands.Checkout(branchRepo, localBranch);
                    }

                    using var finalRepo = new Repository(targetPath);
                    var pluginInfo = new PluginInfo
                    {
                        Name = repoName,
                        Path = targetPath,
                    };
                    SetPluginInfo(pluginInfo, finalRepo);

                    var trackingBranch = finalRepo.Head.TrackedBranch;
                    if (trackingBranch != null)
                        pluginInfo.LatestCommit = trackingBranch.Tip.Sha[..7];

                    _pluginInfo[pluginInfo.Name] = pluginInfo;
                }
                catch (Exception)
                {
                    if (Directory.Exists(targetPath))
                    {
                        try
                        {
                            Directory.Delete(targetPath, true);
                        }
                        catch (Exception ex)
                        {
                            PluginLogger.Error($"Unable to delete {targetPath}: {ex}");
                        }
                    }
                    throw;
                }
            });
        }

        public void DeletePlugin(string pluginName)
        {
            var pluginPath = Path.Combine(_pluginFolder, pluginName);

            if (!Directory.Exists(pluginPath))
                throw new DirectoryNotFoundException($"Plugin directory not found: {pluginPath}");

            try
            {
                PluginLifecycleHelper.TryUnloadPlugin(pluginName);

                GC.Collect();
                GC.WaitForPendingFinalizers();

                foreach (var file in Directory.GetFiles(pluginPath, "*.*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);

                foreach (var dir in Directory.GetDirectories(pluginPath, "*", SearchOption.AllDirectories))
                    File.SetAttributes(dir, FileAttributes.Normal);

                Directory.Delete(pluginPath, true);

                _pluginInfo.TryRemove(pluginName, out _);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to delete plugin: {ex.Message}", ex);
            }
        }

        private void AddManualPlugins()
        {
            foreach (var manualPlugin in GetManualPlugins())
            {
                _pluginInfo[manualPlugin.Name] = manualPlugin;
            }
        }

        public List<PluginInfo> GetPluginInfo()
        {
            return _pluginInfo.Values.ToList();
        }

        public void Dispose()
        {
            _updateCts?.Cancel();
            _updateCts?.Dispose();
            GC.SuppressFinalize(this);
        }

        private readonly CredentialsHandler _credentialsHandler = (url, usernameFromUrl, types) =>
        {
            string username = null;
            string password = null;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git.exe",
                    Arguments = "credential fill",
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                var process = new Process
                {
                    StartInfo = startInfo
                };

                process.Start();

                var uri = new Uri(url);
                process.StandardInput.NewLine = "\n";
                process.StandardInput.WriteLine($"protocol={uri.Scheme}");
                process.StandardInput.WriteLine($"host={uri.Host}");
                process.StandardInput.WriteLine($"path={uri.AbsolutePath}");
                process.StandardInput.WriteLine();


                while (process.StandardOutput.ReadLine() is { } line)
                {
                    var details = line.Split('=');
                    if (details[0] == "username")
                    {
                        username = details[1];
                    }
                    else if (details[0] == "password")
                    {
                        password = details[1];
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLogger.Error($"Unable to retrieve credentials for {url}: {ex}");
                return new DefaultCredentials();
            }

            if (username == null || password == null)
            {
                PluginLogger.Error($"Unable to retrieve credentials for {url}");
                return new DefaultCredentials();
            }

            return new UsernamePasswordCredentials
            {
                Username = username,
                Password = password,
            };
        };

        public async Task ChangeBranchAsync(PluginInfo plugin, string branchName)
        {
            var pluginPath = plugin.Path;

            if (plugin.IsManualInstall)
            {
                throw new InvalidOperationException($"Plugin {plugin.Name} was installed from a release archive and cannot switch branches.");
            }

            PluginLifecycleHelper.TryUnloadPlugin(plugin.Name);

            await Task.Run(() =>
            {
                using var repo = new Repository(pluginPath);
                Branch branch;

                foreach (var remote in repo.Network.Remotes)
                {
                    Commands.Fetch(repo, remote.Name, remote.FetchRefSpecs.Select(r => r.Specification),
                        new FetchOptions { CredentialsProvider = _credentialsHandler }, null);
                }

                // Try to get local branch first
                if (repo.Branches[branchName] is {} literalBranch)
                {
                    if (literalBranch.IsRemote)
                    {
                        branch = repo.Branches.FirstOrDefault(x => GetTrackedBranchSafe(repo, x) == literalBranch);
                        if (branch == null)
                        {
                            var nameParts = literalBranch.FriendlyName.Split("/", 2);
                            var branchNameCandidate = nameParts.Length == 2 ? nameParts[1] : (nameParts[0] + "_local");
                            if (repo.Branches[branchNameCandidate] != null)
                            {
                                branchNameCandidate = literalBranch.FriendlyName.Replace('/', '_');
                            }

                            branch = repo.CreateBranch(branchNameCandidate, literalBranch.Tip);
                            repo.Branches.Update(branch, b => b.TrackedBranch = literalBranch.CanonicalName);
                        }
                    }
                    else
                    {
                        branch = literalBranch;
                    }
                }

                // If local branch doesn't exist, try to create it from remote
                else if (repo.Branches[$"{GetHeadRemote(repo, false).Name}/{branchName}"] is { } remoteBranch)
                {
                    branch = repo.CreateBranch(branchName, remoteBranch.Tip);
                    // Set up tracking
                    repo.Branches.Update(branch,
                        b => b.TrackedBranch = remoteBranch.CanonicalName);
                }
                else
                {
                    throw new Exception($"Branch {branchName} not found locally or remotely");
                }

                Commands.Checkout(repo, branch);
                
                SetPluginInfo(plugin, repo);
            });
        }
    }
}