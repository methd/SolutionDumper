using Microsoft.Win32;
using SolutionDumper.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace SolutionDumper.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public ObservableCollection<TreeNodeViewModel> Roots { get; } = new();
    public ObservableCollection<string> SelectedFiles { get; } = new();

    private string? _slnPath;
    public string? SlnPath
    {
        get => _slnPath;
        private set { _slnPath = value; OnPropertyChanged(); }
    }

    private string _filterText = "";
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (_filterText == value) return;
            _filterText = value;
            OnPropertyChanged();
            ScheduleFilter();
        }
    }

    private int _selectedFileCount;
    public int SelectedFileCount
    {
        get => _selectedFileCount;
        private set { _selectedFileCount = value; OnPropertyChanged(); }
    }

    private readonly DispatcherTimer _statusTimer;

    private string? _statusText;
    public string? StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    private StatusKind _statusKind;
    public StatusKind StatusKind
    {
        get => _statusKind;
        private set { _statusKind = value; OnPropertyChanged(); }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    private long _selectedTotalSize;
    public string SelectedTotalSizeText => $"{_selectedTotalSize / 1024.0 / 1024.0:0.00} MB";

    public RelayCommand OpenSlnCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand CopyToClipboardCommand { get; }

    private readonly ScanOptions _scanOptions = new();
    private string _rootDir = "";

    private readonly DispatcherTimer _filterTimer;
    private readonly DispatcherTimer _rebuildTimer;

    private readonly List<string> _ordered = new(4096);

    private readonly List<string> _projectFiles = new(2048);
    private readonly List<string> _csproj = new(16);
    private readonly List<string> _props = new(512);
    private readonly List<string> _www = new(512);
    private readonly List<string> _rest = new(2048);

    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _sizeCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<TreeNodeViewModel> _filterNodesPostOrder = new(8192);
    private readonly Dictionary<TreeNodeViewModel, int> _filterIndex = new();
    private string[] _filterKeyLower = Array.Empty<string>();
    private int[] _parentIndex = Array.Empty<int>();
    private CancellationTokenSource? _filterCts;

    public MainViewModel()
    {
        OpenSlnCommand = new RelayCommand(_ => OpenSln());
        ExportCommand = new RelayCommand(_ => Export(), _ => SelectedFiles.Count > 0);
        CopyToClipboardCommand = new RelayCommand(_ => CopyToClipboard(), _ => SelectedFiles.Count > 0);

        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            _ = ApplyTreeFilterAsync();
        };

        _rebuildTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _rebuildTimer.Tick += (_, _) =>
        {
            _rebuildTimer.Stop();
            RebuildSelectedFilesCore();
        };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusText = null;
            OnPropertyChanged(nameof(HasStatus));
        };
    }

    private void ScheduleFilter()
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void ScheduleRebuildSelectedFiles()
    {
        if (_rebuildTimer.IsEnabled) return;
        _rebuildTimer.Start();
    }

    private void OpenSln()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Solution (*.sln;*.slnx)|*.sln;*.slnx",
            Title = "Open solution"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            ShowInfo("Loading solution…", ms: 0);
            LoadSolution(dlg.FileName);
        }
        catch (Exception e)
        {
            ShowError($"Failed to load solution: {e.Message}");
        }
    }

    private void LoadSolution(string slnPath)
    {
        Roots.Clear();
        SelectedFiles.Clear();
        _sizeCache.Clear();
        _seen.Clear();

        SlnPath = slnPath;
        _rootDir = Path.GetDirectoryName(Path.GetFullPath(slnPath))!;

        var projects = SolutionParser.Parse(slnPath);

        var solutionName = Path.GetFileNameWithoutExtension(slnPath);
        var rootNode = new TreeNodeViewModel($"Solution '{solutionName}'", null, isFile: false);

        rootNode.CheckedChanged += _ => ScheduleRebuildSelectedFiles();

        foreach (var p in projects)
        {
            var projNode = BuildProjectTree(p);
            rootNode.AddChild(projNode);
        }

        var slnFileNode = new TreeNodeViewModel(Path.GetFileName(slnPath), slnPath, isFile: true);
        rootNode.AddChild(slnFileNode);

        Roots.Add(rootNode);

        RebuildFilterIndex();

        _ = ApplyTreeFilterAsync();
        ScheduleRebuildSelectedFiles();

        ShowSuccess($"Loaded '{solutionName}' ({projects.Count} projects)");
    }

    private TreeNodeViewModel BuildProjectTree(SlnProject p)
    {
        var projNode = new TreeNodeViewModel(p.Name, null, isFile: false);

        var csprojNode = new TreeNodeViewModel(Path.GetFileName(p.CsprojPath), p.CsprojPath, isFile: true);
        projNode.AddChild(csprojNode);

        var projDir = Path.GetDirectoryName(p.CsprojPath)!;

        foreach (var pf in ProjectScanner.EnumerateProjectFiles(p.CsprojPath, _scanOptions))
        {
            bool tooLarge = pf.SizeBytes > _scanOptions.MaxFileSizeBytes;

            var tooltip = tooLarge
                ? $"Excluded by size limit ({pf.SizeBytes / 1024} KB)"
                : null;

            AddPathAsTree(
                projNode,
                projDir,
                pf.FullPath,
                isSelectable: !tooLarge,
                fileSize: pf.SizeBytes,
                tooltip: tooltip);
        }

        projNode.IsChecked = false;
        return projNode;
    }

    private void AddPathAsTree(TreeNodeViewModel projectNode, string projectDir, string fullPath, bool isSelectable, long fileSize, string? tooltip)
    {
        var rel = Path.GetRelativePath(projectDir, fullPath);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var current = projectNode;

        for (int i = 0; i < parts.Length; i++)
        {
            bool isLast = i == parts.Length - 1;
            var name = parts[i];

            if (!isLast)
            {
                TreeNodeViewModel? existingFolder = null;
                for (int j = 0; j < current.Children.Count; j++)
                {
                    var c = current.Children[j];
                    if (!c.IsFile && c.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        existingFolder = c;
                        break;
                    }
                }

                if (existingFolder == null)
                {
                    var folder = new TreeNodeViewModel(name, null, isFile: false, isSelectable, fileSize, tooltip);
                    current.AddChild(folder);
                    current = folder;
                }
                else
                {
                    current = existingFolder;
                }
            }
            else
            {
                bool exists = false;
                for (int j = 0; j < current.Children.Count; j++)
                {
                    var c = current.Children[j];
                    if (c.IsFile && string.Equals(c.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    var fileNode = new TreeNodeViewModel(name, fullPath, isFile: true, isSelectable, fileSize, tooltip);
                    current.AddChild(fileNode);
                }
            }
        }
    }

    private void RebuildFilterIndex()
    {
        _filterNodesPostOrder.Clear();
        _filterIndex.Clear();

        for (int r = 0; r < Roots.Count; r++)
            CollectPostOrder(Roots[r]);

        _filterKeyLower = new string[_filterNodesPostOrder.Count];
        _parentIndex = new int[_filterNodesPostOrder.Count];

        for (int i = 0; i < _filterNodesPostOrder.Count; i++)
            _filterIndex[_filterNodesPostOrder[i]] = i;

        for (int i = 0; i < _filterNodesPostOrder.Count; i++)
        {
            var n = _filterNodesPostOrder[i];

            var dn = n.DisplayName ?? "";
            var fp = n.FullPath ?? "";
            _filterKeyLower[i] = (dn + " " + fp).ToLowerInvariant();

            if (n.Parent != null && _filterIndex.TryGetValue(n.Parent, out var pi))
                _parentIndex[i] = pi;
            else
                _parentIndex[i] = -1;
        }
    }

    private void CollectPostOrder(TreeNodeViewModel node)
    {
        for (int i = 0; i < node.Children.Count; i++)
            CollectPostOrder(node.Children[i]);

        _filterNodesPostOrder.Add(node);
    }

    private async Task ApplyTreeFilterAsync()
    {
        _filterCts?.Cancel();
        _filterCts?.Dispose();
        _filterCts = new CancellationTokenSource();
        var ct = _filterCts.Token;

        if (Roots.Count == 0) return;

        string term = (_filterText ?? "").Trim();

        if (string.IsNullOrWhiteSpace(term))
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ShowAllAndCollapseToCheckedPaths();
            }, DispatcherPriority.Background);

            return;
        }

        var nodes = _filterNodesPostOrder.ToArray();
        var keys = _filterKeyLower;
        var parent = _parentIndex;

        string termLower = term.ToLowerInvariant();

        bool[] visible;
        try
        {
            visible = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                int n = nodes.Length;
                var childHasVisible = new bool[n];
                var vis = new bool[n];

                for (int i = 0; i < n; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    bool selfMatch = keys[i].Contains(termLower);
                    bool isVisible = selfMatch || childHasVisible[i];
                    vis[i] = isVisible;

                    if (isVisible)
                    {
                        int pi = parent[i];
                        if (pi >= 0)
                            childHasVisible[pi] = true;
                    }
                }

                return vis;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            for (int i = 0; i < nodes.Length; i++)
                nodes[i].IsVisible = visible[i];

            for (int r = 0; r < Roots.Count; r++)
                Roots[r].IsVisible = true;

        }, DispatcherPriority.Background);
    }

    private void ShowAllAndCollapseToCheckedPaths()
    {
        for (int i = 0; i < _filterNodesPostOrder.Count; i++)
        {
            var n = _filterNodesPostOrder[i];
            n.IsVisible = true;
        }
        for (int r = 0; r < Roots.Count; r++)
            Roots[r].IsVisible = true;

        for (int i = 0; i < _filterNodesPostOrder.Count; i++)
        {
            var n = _filterNodesPostOrder[i];
            if (n.IsExpanded)
                n.IsExpanded = false;
        }

        for (int i = 0; i < _filterNodesPostOrder.Count; i++)
        {
            var n = _filterNodesPostOrder[i];
            if (n.IsChecked != true) continue;

            var p = n.Parent;
            while (p != null)
            {
                if (!p.IsExpanded)
                    p.IsExpanded = true;
                p = p.Parent;
            }
        }

        for (int r = 0; r < Roots.Count; r++)
        {
            if (!Roots[r].IsExpanded)
                Roots[r].IsExpanded = true;
        }
    }

    private void RebuildSelectedFilesCore()
    {
        SelectedFiles.Clear();
        _ordered.Clear();
        _seen.Clear();

        AppendCheckedSlnFirst(_ordered);

        for (int r = 0; r < Roots.Count; r++)
        {
            var root = Roots[r];

            for (int i = 0; i < root.Children.Count; i++)
            {
                var child = root.Children[i];
                if (child.IsFile) continue;

                _projectFiles.Clear();
                CollectCheckedFiles(child, _projectFiles);
                if (_projectFiles.Count == 0) continue;

                string? projectDir = null;
                for (int k = 0; k < _projectFiles.Count; k++)
                {
                    var p = _projectFiles[k];
                    if (p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        projectDir = Path.GetDirectoryName(p);
                        break;
                    }
                }
                projectDir ??= Path.GetDirectoryName(_projectFiles[0]);

                _csproj.Clear();
                _props.Clear();
                _www.Clear();
                _rest.Clear();

                for (int k = 0; k < _projectFiles.Count; k++)
                {
                    var path = _projectFiles[k];

                    if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        _csproj.Add(path);
                        continue;
                    }

                    var rel = SafeRel(projectDir!, path);
                    if (rel.StartsWith("Properties/", StringComparison.OrdinalIgnoreCase))
                        _props.Add(path);
                    else if (rel.StartsWith("wwwroot/", StringComparison.OrdinalIgnoreCase))
                        _www.Add(path);
                    else
                        _rest.Add(path);
                }

                _csproj.Sort(StringComparer.OrdinalIgnoreCase);

                var relComparer = new RelPathComparer(projectDir!);
                _props.Sort(relComparer);
                _www.Sort(relComparer);
                _rest.Sort(relComparer);

                AppendUnique(_csproj);
                AppendUnique(_props);
                AppendUnique(_www);
                AppendUnique(_rest);
            }
        }

        for (int i = 0; i < _ordered.Count; i++)
            SelectedFiles.Add(_ordered[i]);

        SelectedFileCount = SelectedFiles.Count;

        _selectedTotalSize = 0;
        for (int i = 0; i < _ordered.Count; i++)
        {
            var f = _ordered[i];
            _selectedTotalSize += GetFileSizeCached(f);
        }
        OnPropertyChanged(nameof(SelectedTotalSizeText));

        ExportCommand.RaiseCanExecuteChanged();
        CopyToClipboardCommand.RaiseCanExecuteChanged();
    }

    private void AppendCheckedSlnFirst(List<string> ordered)
    {
        for (int r = 0; r < Roots.Count; r++)
        {
            var root = Roots[r];
            for (int i = 0; i < root.Children.Count; i++)
            {
                var child = root.Children[i];
                if (!child.IsFile) continue;

                var path = child.FullPath;
                if (path == null) continue;

                if (!(path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (child.IsChecked == true)
                {
                    if (_seen.Add(path))
                        ordered.Add(path);
                }
            }
        }
    }

    private void AppendUnique(List<string> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var f = list[i];
            if (_seen.Add(f))
                _ordered.Add(f);
        }
    }

    private static void CollectCheckedFiles(TreeNodeViewModel node, List<string> files)
    {
        if (node.IsFile && node.IsChecked == true && node.FullPath != null)
            files.Add(node.FullPath);

        for (int i = 0; i < node.Children.Count; i++)
            CollectCheckedFiles(node.Children[i], files);
    }

    private long GetFileSizeCached(string path)
    {
        if (_sizeCache.TryGetValue(path, out var size))
            return size;

        try
        {
            size = new FileInfo(path).Length;
        }
        catch
        {
            size = 0;
        }

        _sizeCache[path] = size;
        return size;
    }

    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Text (*.txt)|*.txt|Markdown (*.md)|*.md",
            FileName = "code_dump.txt"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            ShowInfo("Exporting…", ms: 0);

            using var fs = new FileStream(dlg.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
            using var w = new StreamWriter(fs, System.Text.Encoding.UTF8);

            WriteDump(w);

            ShowSuccess($"Exported ({SelectedFiles.Count} files)");
        }
        catch (Exception e)
        {
            ShowError($"Export failed: {e.Message}");
        }
    }

    private void CopyToClipboard()
    {
        try
        {
            using var sw = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            WriteDump(sw);

            Clipboard.SetText(sw.ToString());
            ShowSuccess($"Copied ({SelectedFiles.Count} files)");
        }
        catch (Exception e)
        {
            ShowError($"Failed to copy: {e.Message}");
        }
    }

    private void WriteDump(TextWriter w)
    {
        char[] buffer = new char[32 * 1024];

        w.WriteLine($"### CODE DUMP GENERATED: {DateTime.Now:O}");
        w.WriteLine($"### SLN: {Path.GetFileName(SlnPath)}");
        w.WriteLine($"### FILE COUNT: {SelectedFiles.Count}");

        for (int i = 0; i < SelectedFiles.Count; i++)
        {
            var file = SelectedFiles[i];
            var rel = SafeRel(_rootDir, file);

            w.WriteLine();
            w.WriteLine();
            w.WriteLine($"===== FILE: {rel} =====");

            try
            {
                using var r = new StreamReader(file);
                int read;
                while ((read = r.Read(buffer, 0, buffer.Length)) > 0)
                    w.Write(buffer, 0, read);
            }
            catch (Exception e)
            {
                w.WriteLine($"[[FAILED TO READ FILE]] {e.Message}");
            }
        }
    }

    private static string SafeRel(string root, string file)
    {
        try { return Path.GetRelativePath(root, file).Replace('\\', '/'); }
        catch { return file.Replace('\\', '/'); }
    }

    private void ShowInfo(string text, int ms = 1500) => ShowStatus(text, StatusKind.Info, ms);
    private void ShowSuccess(string text, int ms = 1500) => ShowStatus(text, StatusKind.Success, ms);
    private void ShowWarning(string text, int ms = 2000) => ShowStatus(text, StatusKind.Warning, ms);
    private void ShowError(string text, int ms = 3000) => ShowStatus(text, StatusKind.Error, ms);

    private void ShowStatus(string text, StatusKind kind, int ms)
    {
        StatusKind = kind;
        StatusText = text;
        OnPropertyChanged(nameof(HasStatus));

        if (ms <= 0)
        {
            _statusTimer.Stop();
            return;
        }

        _statusTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private sealed class RelPathComparer : IComparer<string>
    {
        private readonly string _projectDir;
        public RelPathComparer(string projectDir) => _projectDir = projectDir;

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var rx = SafeRel(_projectDir, x);
            var ry = SafeRel(_projectDir, y);
            return StringComparer.OrdinalIgnoreCase.Compare(rx, ry);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
