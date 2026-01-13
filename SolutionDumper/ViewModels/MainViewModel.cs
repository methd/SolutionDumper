using Microsoft.Win32;
using SolutionDumper.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
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

    private long _selectedTotalSize;
    public string SelectedTotalSizeText => $"{_selectedTotalSize / 1024.0 / 1024.0:0.00} MB";

    public RelayCommand OpenSlnCommand { get; }
    public RelayCommand ExportCommand { get; }

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

    public MainViewModel()
    {
        OpenSlnCommand = new RelayCommand(_ => OpenSln());
        ExportCommand = new RelayCommand(_ => Export(), _ => SelectedFiles.Count > 0);

        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            ApplyTreeFilter();
        };

        _rebuildTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _rebuildTimer.Tick += (_, _) =>
        {
            _rebuildTimer.Stop();
            RebuildSelectedFilesCore();
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

        LoadSolution(dlg.FileName);
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

        ApplyTreeFilter();
        ScheduleRebuildSelectedFiles();
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

    private void ApplyTreeFilter()
    {
        string term = (_filterText ?? "").Trim();
        if (Roots.Count == 0) return;

        if (string.IsNullOrWhiteSpace(term))
        {
            for (int i = 0; i < Roots.Count; i++)
                SetVisibleRecursive(Roots[i], true);
            return;
        }

        for (int i = 0; i < Roots.Count; i++)
        {
            var r = Roots[i];
            UpdateVisibilityByFilter(r, term);
            r.IsVisible = true;
            r.IsExpanded = true;
        }
    }

    private static void SetVisibleRecursive(TreeNodeViewModel node, bool visible)
    {
        node.IsVisible = visible;
        for (int i = 0; i < node.Children.Count; i++)
            SetVisibleRecursive(node.Children[i], visible);
    }

    private static bool UpdateVisibilityByFilter(TreeNodeViewModel node, string term)
    {
        bool selfMatch =
            node.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (node.FullPath != null && node.FullPath.Contains(term, StringComparison.OrdinalIgnoreCase));

        bool anyChildVisible = false;
        for (int i = 0; i < node.Children.Count; i++)
        {
            if (UpdateVisibilityByFilter(node.Children[i], term))
                anyChildVisible = true;
        }

        node.IsVisible = selfMatch || anyChildVisible;
        if (node.IsVisible)
            node.IsExpanded = true;

        return node.IsVisible;
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

        var outPath = dlg.FileName;
        using var w = new StreamWriter(outPath, false, System.Text.Encoding.UTF8);

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
                w.Write(File.ReadAllText(file));
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
