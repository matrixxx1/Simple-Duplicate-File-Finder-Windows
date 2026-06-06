using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;

namespace SimpleDuplicateFileFinder;

public partial class MainWindow : Window
{
    private readonly List<DuplicateGroupViewModel> _scanResults = new();
    private readonly ObservableCollection<DuplicateGroupViewModel> _displayedGroups = new();
    private CancellationTokenSource? _scanCancellation;

    public MainWindow()
    {
        InitializeComponent();
        GroupsList.ItemsSource = _displayedGroups;
        ActivityList.ItemsSource = new ObservableCollection<string>();
        FileGrid.ItemsSource = null;
        SummaryText.Text = "Ready. Add folders and run scan.";
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

            var matchedFiles = await Task.Run(
                () => EnumerateFiles(folders, minBytes, extensionFilter, token),
                token);

            token.ThrowIfCancellationRequested();
            AddActivity($"Found {matchedFiles.Count:n0} candidate files after size/type filters.");

            var sizeBuckets = matchedFiles
                .GroupBy(x => x.SizeBytes)
                .Where(g => g.Count() > 1)
                .ToList();

            AddActivity($"Size bucketing identified {sizeBuckets.Count:n0} potential duplicate groups.");

            var groups = await Task.Run(
                () => HashAndBuildGroups(sizeBuckets, token),
                token);

            _scanResults.AddRange(groups);
            ApplySorting();
            AddActivity(_scanResults.Count > 0
                ? $"Found {_scanResults.Count} duplicate set(s)."
                : "No duplicate sets found.");
            SummaryText.Text = _scanResults.Count > 0
                ? "Scan complete. Select a group to review files."
                : "No duplicate sets for your current filters.";
        }
        catch (OperationCanceledException)
        {
            AddActivity("Scan was canceled.");
            SummaryText.Text = "Scan canceled.";
        }
        catch (Exception ex)
        {
            AddActivity($"Scan failed: {ex.Message}");
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
        }
    }

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "Select folder to scan" };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        if (!FoldersList.Items.Cast<string>().Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase))
        {
            FoldersList.Items.Add(dlg.SelectedPath);
            AddActivity($"Added folder: {dlg.SelectedPath}");
        }
        else
        {
            AddActivity("Folder already added.");
        }
    }

    private void OnClearFoldersClick(object sender, RoutedEventArgs e)
    {
        FoldersList.Items.Clear();
        AddActivity("Folder list cleared.");
    }

    private void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        var selected = FoldersList.SelectedItem as string;
        if (selected is null)
        {
            AddActivity("Select a folder to remove.");
            return;
        }

        FoldersList.Items.Remove(selected);
        AddActivity($"Removed folder: {selected}");
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
    }

    private void OnKeepNewestClick(object sender, RoutedEventArgs e) => ApplyRetentionStrategy(keepNewest: true);
    private void OnKeepOldestClick(object sender, RoutedEventArgs e) => ApplyRetentionStrategy(keepNewest: false);

    private async void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        var candidates = GetMarkedFiles();
        if (!candidates.Any())
        {
            AddActivity("No files marked for deletion.");
            return;
        }

        AddActivity($"Deleting {candidates.Count} file(s)...");
        await Task.Run(() =>
        {
            foreach (var item in candidates)
            {
                try
                {
                    File.Delete(item.Path);
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AddActivity($"Failed delete: {Path.GetFileName(item.Path)} - {ex.Message}"));
                }
            }
        });

        RefreshAfterMutations();
        AddActivity("Deletion pass finished.");
    }

    private async void OnMoveToQuarantineClick(object sender, RoutedEventArgs e)
    {
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
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AddActivity($"Move failed: {Path.GetFileName(item.Path)} - {ex.Message}"));
                }
            }
        });

        RefreshAfterMutations();
        AddActivity("Move-to-quarantine pass finished.");
    }

    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupsList.SelectedItem is not DuplicateGroupViewModel selected)
        {
            FileGrid.ItemsSource = null;
            return;
        }

        FileGrid.ItemsSource = selected.Files;
        SummaryText.Text = $"{selected.Files.Count} files • {FormatSize(selected.SizeBytes)} each • Wasted: {FormatSize(selected.WastedBytes)}";
    }

    private void ApplyRetentionStrategy(bool keepNewest)
    {
        if (GroupsList.SelectedItem is not DuplicateGroupViewModel selected)
        {
            AddActivity("Select one duplicate group first.");
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
                string extension = Path.GetExtension(file);

                try
                {
                    var info = new FileInfo(file);
                    if (info.Length < minBytes || (extensionFilter.Count > 0 && !extensionFilter.Contains("*.*") && !extensionFilter.Contains(extension.ToLowerInvariant())))
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
                    list = new List<FileScanItem>();
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
        if (_displayedGroups.Count > 0 && FileGrid.ItemsSource is not null)
        {
            FileGrid.ItemsSource = _displayedGroups.First().Files;
        }

        SummaryText.Text = _scanResults.Count == 0
            ? "No duplicate sets remain."
            : $"{_scanResults.Count} duplicate set(s) remain.";
    }

    private void AddActivity(string message)
    {
        if (ActivityList.ItemsSource is ObservableCollection<string> list)
        {
            list.Insert(0, $"{DateTime.Now:HH:mm:ss} {message}");
            if (list.Count > 200)
            {
                list.RemoveAt(list.Count - 1);
            }
        }
        StatusText.Text = message;
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
            SizeBytes = sizeBytes;
        }

        public string Hash { get; }
        public long SizeBytes { get; }
        public long WastedBytes { get; set; }
        public ObservableCollection<FileRow> Files { get; }
        public string DisplayName => $"{Files.Count} files • {FormatSize(SizeBytes)} • {FormatSize(WastedBytes)} wasted";
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
}
