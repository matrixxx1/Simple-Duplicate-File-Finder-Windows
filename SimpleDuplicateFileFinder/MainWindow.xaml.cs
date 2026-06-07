using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;

namespace SimpleDuplicateFileFinder;

public partial class MainWindow : Window
{
    private const int TrialDays = 15;
    private const string FullLicenseSample = "SDF-FULL-UNLOCK-2026";

    private readonly List<DuplicateGroupViewModel> _scanResults = [];
    private readonly ObservableCollection<DuplicateGroupViewModel> _displayedGroups = [];
    private readonly FileLogger _logger;
    private CancellationTokenSource? _scanCancellation;
    private AppState _state;
    private readonly string _stateFilePath;

    public MainWindow()
    {
        InitializeComponent();
        _stateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleDuplicateFileFinder",
            "state.json");

        _logger = new FileLogger();
        _state = LoadState();

        GroupsList.ItemsSource = _displayedGroups;
        ActivityList.ItemsSource = new ObservableCollection<string>();
        FileGrid.ItemsSource = null;

        SummaryText.Text = "Ready. Add folders and run scan.";
        RefreshTrialUi();
        AddActivity("Duplicate finder initialized.");
    }

    private async void OnRunScanClick(object sender, RoutedEventArgs e)
    {
        if (!FoldersList.Items.Cast<string>().Any())
        {
            AddActivity("Add at least one folder before scanning.");
            return;
        }

        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;

        _scanResults.Clear();
        _displayedGroups.Clear();
        GroupsList.ItemsSource = _displayedGroups;
        FileGrid.ItemsSource = null;
        SummaryText.Text = "Scanning files...";

        try
        {
            var folders = FoldersList.Items.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var minBytes = ParseLong(MinSizeTextBox.Text) * 1024L;
            var extensionFilter = ParseExtensions(ExtensionFilterTextBox.Text);

            AddActivity($"Queued {folders.Length} folder(s).");
            LogAudit("Run scan started.");

            var matchedFiles = await Task.Run(
                () => EnumerateFiles(folders, minBytes, extensionFilter, token),
                token);

            token.ThrowIfCancellationRequested();
            AddActivity($"Found {matchedFiles.Count:n0} candidate files after filters.");

            var sizeBuckets = matchedFiles
                .GroupBy(x => x.SizeBytes)
                .Where(g => g.Count() > 1)
                .ToList();

            AddActivity($"Size bucketing found {sizeBuckets.Count:n0} possible sets.");

            var groups = await Task.Run(
                () => HashAndBuildGroups(sizeBuckets, token),
                token);

            _scanResults.AddRange(groups);
            ApplySorting();
            LogAudit($"Scan completed. Duplicate sets: {_scanResults.Count}");
            AddActivity(_scanResults.Count > 0
                ? $"Found {_scanResults.Count} duplicate set(s)."
                : "No duplicate sets found.");
            SummaryText.Text = _scanResults.Count > 0
                ? "Scan complete. Select a group to review files."
                : "No duplicate sets for current filters.";
        }
        catch (OperationCanceledException)
        {
            AddActivity("Scan canceled.");
            LogAudit("Scan canceled by user.");
            SummaryText.Text = "Scan canceled.";
        }
        catch (Exception ex)
        {
            AddActivity($"Scan failed: {ex.Message}");
            LogAudit($"Scan failed: {ex}");
            SummaryText.Text = "Scan failed.";
        }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
        }
    }

    private void OnCancelScanClick(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation != null)
        {
            _scanCancellation.Cancel();
            AddActivity("Cancel requested.");
            LogAudit("Scan cancel requested.");
        }
        else
        {
            AddActivity("No active scan to cancel.");
        }
    }

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "Select folder to scan" };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var selected = dlg.SelectedPath;
        if (!FoldersList.Items.Cast<string>().Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            FoldersList.Items.Add(selected);
            AddActivity($"Added folder: {selected}");
            LogAudit($"Folder added: {selected}");
        }
        else
        {
            AddActivity("Folder already in list.");
        }
    }

    private void OnClearFoldersClick(object sender, RoutedEventArgs e)
    {
        FoldersList.Items.Clear();
        AddActivity("Folder list cleared.");
        LogAudit("Folder list cleared.");
    }

    private void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if (FoldersList.SelectedItem is not string selected)
        {
            AddActivity("Select a folder to remove.");
            return;
        }

        FoldersList.Items.Remove(selected);
        AddActivity($"Removed folder: {selected}");
        LogAudit($"Folder removed: {selected}");
    }

    private void OnExportReportClick(object sender, RoutedEventArgs e)
    {
        if (_scanResults.Count == 0)
        {
            AddActivity("No scan results to export.");
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = Path.Combine(desktop, $"simple-duplicates-report-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.csv");
        using var writer = new StreamWriter(path);
        writer.WriteLine("GroupId,Hash,SizeBytes,Path,LastModified,Delete");
        for (var i = 0; i < _scanResults.Count; i++)
        {
            var group = _scanResults[i];
            foreach (var file in group.Files)
            {
                writer.WriteLine($"{i + 1},\"{group.Hash}\",{group.SizeBytes},\"{file.Path.Replace("\"", "\"\"")}\",{file.LastModified},\"{file.MarkForDeletion}\"");
            }
        }

        AddActivity($"Report exported: {path}");
        LogAudit($"Exported duplicate report: {path}");
    }

    private void OnKeepNewestClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureCleanupAllowed())
        {
            return;
        }

        ApplyRetentionStrategy(keepNewest: true);
    }

    private void OnKeepOldestClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureCleanupAllowed())
        {
            return;
        }

        ApplyRetentionStrategy(keepNewest: false);
    }

    private async void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureCleanupAllowed())
        {
            return;
        }

        var candidates = GetMarkedFiles();
        if (!candidates.Any())
        {
            AddActivity("No files marked for deletion.");
            return;
        }

        AddActivity($"Deleting {candidates.Count} file(s)...");
        LogAudit($"Delete selected started. Count={candidates.Count}");
        await Task.Run(() =>
        {
            foreach (var item in candidates)
            {
                try
                {
                    File.Delete(item.Path);
                    LogAudit($"Deleted file: {item.Path}");
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AddActivity($"Failed delete: {Path.GetFileName(item.Path)} - {ex.Message}"));
                    LogAudit($"Delete failed: {item.Path} - {ex.Message}");
                }
            }
        });

        RefreshAfterMutations();
        AddActivity("Deletion pass finished.");
        LogAudit("Delete selected finished.");
    }

    private async void OnMoveToQuarantineClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureCleanupAllowed())
        {
            return;
        }

        var candidates = GetMarkedFiles();
        if (!candidates.Any())
        {
            AddActivity("No files marked for quarantine.");
            return;
        }

        var quarantineRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleDuplicateFileFinder",
            "Quarantine");
        Directory.CreateDirectory(quarantineRoot);
        AddActivity("Moving selected files to quarantine.");

        await Task.Run(() =>
        {
            foreach (var item in candidates)
            {
                var targetPath = Path.Combine(quarantineRoot, Path.GetFileName(item.Path));
                if (File.Exists(targetPath))
                {
                    targetPath = Path.Combine(quarantineRoot, $"{Path.GetFileNameWithoutExtension(item.Path)}-{Guid.NewGuid():N}{Path.GetExtension(item.Path)}");
                }

                try
                {
                    File.Move(item.Path, targetPath);
                    LogAudit($"Moved to quarantine: {item.Path} -> {targetPath}");
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AddActivity($"Move failed: {Path.GetFileName(item.Path)} - {ex.Message}"));
                    LogAudit($"Move failed: {item.Path} - {ex.Message}");
                }
            }
        });

        RefreshAfterMutations();
        AddActivity("Move-to-quarantine pass finished.");
        LogAudit("Move selected finished.");
    }

    private void OnActivateLicenseClick(object sender, RoutedEventArgs e)
    {
        var candidate = LicenseKeyTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            AddActivity("Enter a license key or continue with trial.");
            return;
        }

        if (!string.Equals(candidate, FullLicenseSample, StringComparison.OrdinalIgnoreCase))
        {
            AddActivity("Invalid license key. Use a valid key to unlock full version.");
            LogAudit("License activation failed.");
            return;
        }

        _state.FullVersion = true;
        _state.LicenseKey = candidate;
        SaveState(_state);
        RefreshTrialUi();
        AddActivity("License activated. Full version unlocked.");
        LogAudit("License activated.");
        LicenseKeyTextBox.Text = string.Empty;
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow(
            isTrialMode: !_state.FullVersion,
            trialDaysLeft: Math.Max(0, (int)Math.Ceiling((_state.InstallDateUtc.AddDays(TrialDays) - DateTime.UtcNow).TotalDays)),
            isTrialExpired: IsTrialExpired(_state),
            appVersion: "1.0.0");
        about.Owner = this;
        about.ShowDialog();
        LogAudit("Opened about dialog.");
    }

    private void OnViewLogsClick(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(_logger.LogPath);
        if (folder is null || !Directory.Exists(folder))
        {
            AddActivity("No logs found yet.");
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        LogAudit("Opened log folder.");
    }

    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupsList.SelectedItem is not DuplicateGroupViewModel selected)
        {
            FileGrid.ItemsSource = null;
            return;
        }

        FileGrid.ItemsSource = selected.Files;
        SummaryText.Text = $"{selected.Files.Count} files, each {FormatSize(selected.SizeBytes)}. Wasted space: {FormatSize(selected.WastedBytes)}.";
    }

    private bool EnsureCleanupAllowed()
    {
        if (!IsTrialExpired(_state))
        {
            return true;
        }

        if (_state.FullVersion)
        {
            return true;
        }

        AddActivity("Trial expired. Enter a full license key to unlock cleanup actions.");
        RefreshTrialUi();
        return false;
    }

    private void ApplyRetentionStrategy(bool keepNewest)
    {
        if (GroupsList.SelectedItem is not DuplicateGroupViewModel selected)
        {
            AddActivity("Select one duplicate set first.");
            return;
        }

        var ordered = selected.Files.OrderBy(f => f.LastModified).ToList();
        if (keepNewest)
        {
            ordered = ordered.OrderByDescending(f => f.LastModified).ToList();
        }

        var keep = ordered.First();
        foreach (var file in selected.Files)
        {
            file.MarkForDeletion = !ReferenceEquals(file, keep);
        }

        FileGrid.Items.Refresh();
        AddActivity(keepNewest
            ? "Retention set: keep newest file in selected set."
            : "Retention set: keep oldest file in selected set.");
        LogAudit("Retention action applied.");
    }

    private List<FileRow> GetMarkedFiles()
    {
        return _scanResults.SelectMany(g => g.Files).Where(f => f.MarkForDeletion).ToList();
    }

    private List<FileScanItem> EnumerateFiles(string[] folders, long minBytes, HashSet<string> extensionFilter, CancellationToken token)
    {
        var items = new List<FileScanItem>();
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                AddActivity($"Missing folder skipped: {folder}");
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                string extension = Path.GetExtension(file).ToLowerInvariant();

                try
                {
                    var info = new FileInfo(file);
                    if (info.Length < minBytes || (extensionFilter.Count > 0 && !extensionFilter.Contains("*.*") && !extensionFilter.Contains(extension)))
                    {
                        continue;
                    }

                    items.Add(new FileScanItem
                    {
                        Path = info.FullName,
                        SizeBytes = info.Length,
                        LastModified = info.LastWriteTime,
                        Hash = string.Empty
                    });
                }
                catch
                {
                    AddActivity($"Skipped inaccessible file: {file}");
                    LogAudit($"Skipped inaccessible file: {file}");
                }
            }
        }

        return items;
    }

    private List<DuplicateGroupViewModel> HashAndBuildGroups(List<IGrouping<long, FileScanItem>> sizeBuckets, CancellationToken token)
    {
        var results = new List<DuplicateGroupViewModel>();
        using var sha = SHA256.Create();

        foreach (var bucket in sizeBuckets)
        {
            token.ThrowIfCancellationRequested();
            var byHash = new Dictionary<string, List<FileScanItem>>();

            foreach (var file in bucket)
            {
                token.ThrowIfCancellationRequested();
                var hash = ComputeHash(file.Path, sha);
                file.Hash = hash;

                if (!byHash.TryGetValue(hash, out var list))
                {
                    list = [];
                    byHash[hash] = list;
                }
                list.Add(file);
            }

            foreach (var hashBucket in byHash.Values.Where(x => x.Count > 1))
            {
                results.Add(new DuplicateGroupViewModel(hashBucket[0].Hash, bucket.Key, hashBucket));
            }
        }

        return results;
    }

    private static string ComputeHash(string filePath, HashAlgorithm sha)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private void ApplySorting()
    {
        var ordered = SortModeComboBox.SelectedIndex switch
        {
            1 => _scanResults.OrderByDescending(g => g.SizeBytes),
            2 => _scanResults.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => _scanResults.OrderByDescending(g => g.WastedBytes)
        };

        _displayedGroups.Clear();
        foreach (var group in ordered)
        {
            _displayedGroups.Add(group);
        }

        AddActivity($"Displaying {_displayedGroups.Count} duplicate set(s).");
    }

    private void RefreshAfterMutations()
    {
        var toRemove = new List<DuplicateGroupViewModel>();
        foreach (var group in _scanResults)
        {
            for (var i = group.Files.Count - 1; i >= 0; i--)
            {
                if (group.Files[i].MarkForDeletion)
                {
                    group.Files.RemoveAt(i);
                }
            }

            if (group.Files.Count < 2)
            {
                toRemove.Add(group);
            }
            else
            {
                group.WastedBytes = (group.Files.Count - 1) * group.SizeBytes;
            }
        }

        foreach (var dead in toRemove)
        {
            _scanResults.Remove(dead);
        }

        ApplySorting();
        if (_displayedGroups.Count > 0 && FileGrid.ItemsSource is not null && GroupsList.SelectedItem is not null)
        {
            FileGrid.ItemsSource = (_displayedGroups.FirstOrDefault(g => ReferenceEquals(g, (DuplicateGroupViewModel)GroupsList.SelectedItem)) ?? _displayedGroups.First()).Files;
        }

        SummaryText.Text = _scanResults.Count == 0
            ? "No duplicate sets remain."
            : $"{_scanResults.Count} duplicate set(s) remain.";
    }

    private void RefreshTrialUi()
    {
        var isExpired = IsTrialExpired(_state);
        var isFull = _state.FullVersion;

        if (isFull)
        {
            TrialStatusText.Text = "License: Full Version";
            DeleteButton.IsEnabled = true;
            QuarantineButton.IsEnabled = true;
            KeepNewest.IsEnabled = true;
            KeepOldest.IsEnabled = true;
            return;
        }

        if (isExpired)
        {
            var installed = _state.InstallDateUtc.ToLocalTime();
            TrialStatusText.Text = $"Trial expired on {installed.AddDays(TrialDays):d} . Enter license key to continue cleanup.";
            DeleteButton.IsEnabled = false;
            QuarantineButton.IsEnabled = false;
            return;
        }

        var remaining = Math.Max(0, (int)Math.Ceiling((_state.InstallDateUtc.AddDays(TrialDays) - DateTime.UtcNow).TotalDays));
        TrialStatusText.Text = $"Trial version: {remaining} of {TrialDays} days remaining (local).";
        DeleteButton.IsEnabled = true;
        QuarantineButton.IsEnabled = true;
        KeepNewest.IsEnabled = true;
        KeepOldest.IsEnabled = true;
    }

    private static bool IsTrialExpired(AppState state)
    {
        if (state.FullVersion)
        {
            return false;
        }

        return DateTime.UtcNow >= state.InstallDateUtc.AddDays(TrialDays);
    }

    private void AddActivity(string message)
    {
        if (ActivityList.ItemsSource is ObservableCollection<string> list)
        {
            list.Insert(0, $"{DateTime.Now:HH:mm:ss} {message}");
            if (list.Count > 250)
            {
                list.RemoveAt(list.Count - 1);
            }
        }

        StatusText.Text = message;
        _logger.Log(message);
    }

    private void LogAudit(string message)
    {
        _logger.Log($"AUDIT: {message}");
    }

    private static long ParseLong(string raw)
    {
        return long.TryParse(raw, out var value) && value >= 0 ? value : 0;
    }

    private static HashSet<string> ParseExtensions(string raw)
    {
        var entries = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Select(x => x.Equals("*.*", StringComparison.OrdinalIgnoreCase)
                ? "*.*"
                : x.StartsWith('.') ? x.ToLowerInvariant() : $".{x.ToLowerInvariant()}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return entries.Count == 0 ? new HashSet<string> { "*.*" } : entries;
    }

    private AppState LoadState()
    {
        var defaultState = new AppState
        {
            InstallDateUtc = DateTime.UtcNow,
            FullVersion = false,
            LicenseKey = null
        };

        try
        {
            if (!File.Exists(_stateFilePath))
            {
                SaveState(defaultState);
                return defaultState;
            }

            var raw = File.ReadAllText(_stateFilePath);
            var parsed = JsonSerializer.Deserialize<AppState>(raw);
            if (parsed is null)
            {
                SaveState(defaultState);
                return defaultState;
            }

            parsed.InstallDateUtc = parsed.InstallDateUtc == default ? DateTime.UtcNow : parsed.InstallDateUtc;
            return parsed;
        }
        catch
        {
            SaveState(defaultState);
            return defaultState;
        }
    }

    private void SaveState(AppState state)
    {
        var folder = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_stateFilePath, json);
    }

    private sealed class FileScanItem
    {
        public required string Path { get; init; }
        public required long SizeBytes { get; init; }
        public required DateTime LastModified { get; init; }
        public required string Hash { get; set; }
    }

    private sealed class DuplicateGroupViewModel
    {
        public DuplicateGroupViewModel(string hash, long sizeBytes, IEnumerable<FileScanItem> files)
        {
            Hash = hash;
            SizeBytes = sizeBytes;
            Files = new ObservableCollection<FileRow>(files.Select(file => new FileRow
            {
                Path = file.Path,
                SizeText = FormatSize(file.SizeBytes),
                LastModified = file.LastModified,
                HashPrefix = file.Hash[..Math.Min(file.Hash.Length, 12)],
                MarkForDeletion = false
            }));
            WastedBytes = (Files.Count - 1) * sizeBytes;
        }

        public string Hash { get; }
        public long SizeBytes { get; }
        public long WastedBytes { get; set; }
        public ObservableCollection<FileRow> Files { get; }
        public string DisplayName => $"{Files.Count} files - {FormatSize(SizeBytes)} each - {FormatSize(WastedBytes)} wasted";
    }

    private sealed class FileRow : INotifyPropertyChanged
    {
        private bool _markForDeletion;

        public string Path { get; init; } = "";
        public string SizeText { get; init; } = "";
        public DateTime LastModified { get; init; }
        public string HashPrefix { get; init; } = "";

        public bool MarkForDeletion
        {
            get => _markForDeletion;
            set
            {
                if (_markForDeletion == value)
                {
                    return;
                }
                _markForDeletion = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MarkForDeletion)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static string FormatSize(long bytes)
    {
        string[] unit = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < unit.Length - 1)
        {
            value /= 1024;
            i++;
        }

        return $"{value:F1} {unit[i]}";
    }

    private sealed class AppState
    {
        public DateTime InstallDateUtc { get; set; }
        public bool FullVersion { get; set; }
        public string? LicenseKey { get; set; }
    }

    private sealed class FileLogger
    {
        private readonly string _filePath;
        private readonly string _folder;
        private const long MaxLogSizeBytes = 4_000_000;

        public FileLogger()
        {
            _folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimpleDuplicateFileFinder",
                "Logs");
            Directory.CreateDirectory(_folder);
            _filePath = Path.Combine(_folder, "simpleduplicatefilefinder.log");
        }

        public string LogPath => _filePath;

        public void Log(string message)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_filePath, $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Never block app flow for logging issues.
            }
        }

        private void RotateIfNeeded()
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var file = new FileInfo(_filePath);
            if (file.Length <= MaxLogSizeBytes)
            {
                return;
            }

            var archive = Path.Combine(_folder, $"simpleduplicatefilefinder_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.Move(_filePath, archive, true);
        }
    }
}
