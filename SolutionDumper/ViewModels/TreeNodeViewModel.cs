using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SolutionDumper.ViewModels;

public sealed class TreeNodeViewModel : INotifyPropertyChanged
{
    public string DisplayName { get; }
    public string? FullPath { get; }
    public bool IsFile { get; }

    public long? FileSizeBytes { get; }

    public bool IsSelectable { get; }

    public string? Tooltip { get; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();
    public bool HasChildren => Children.Count > 0;

    private bool? _isChecked = false;
    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (!IsSelectable)
                return;

            if (_isChecked == value) return;

            _isChecked = value;
            OnPropertyChanged();

            if (value is true or false)
                SetChildrenChecked(value.Value);

            Parent?.UpdateFromChildren();
            BubbleCheckedChanged();
        }
    }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set { if (_isVisible == value) return; _isVisible = value; OnPropertyChanged(); }
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); }
    }

    public TreeNodeViewModel? Parent { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<TreeNodeViewModel>? CheckedChanged;

    public TreeNodeViewModel(
        string displayName,
        string? fullPath,
        bool isFile,
        bool isSelectable = true,
        long? fileSizeBytes = null,
        string? tooltip = null)
    {
        DisplayName = displayName;
        FullPath = fullPath;
        IsFile = isFile;
        IsSelectable = isSelectable;
        FileSizeBytes = fileSizeBytes;
        Tooltip = tooltip;

        Children.CollectionChanged += OnChildrenChanged;
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(HasChildren));

    public void AddChild(TreeNodeViewModel child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    private void SetChildrenChecked(bool value)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i].SetCheckedInternal(value);
    }

    private void SetCheckedInternal(bool value)
    {
        if (!IsSelectable)
            return;

        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));

        for (int i = 0; i < Children.Count; i++)
            Children[i].SetCheckedInternal(value);
    }

    private void UpdateFromChildren()
    {
        if (Children.Count == 0) return;

        bool allTrue = true;
        bool allFalse = true;

        for (int i = 0; i < Children.Count; i++)
        {
            var v = Children[i].IsChecked;
            if (v != true) allTrue = false;
            if (v != false) allFalse = false;
            if (!allTrue && !allFalse) break;
        }

        bool? newValue = allTrue ? true : allFalse ? false : null;
        if (_isChecked == newValue) return;

        _isChecked = newValue;
        OnPropertyChanged(nameof(IsChecked));
        Parent?.UpdateFromChildren();
    }

    private void BubbleCheckedChanged()
    {
        CheckedChanged?.Invoke(this);
        Parent?.BubbleCheckedChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
