using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.UI.Features.SoftwareApps.ViewModels;

/// <summary>A resource tree with selectable branches, indeterminate parents and exact leaf identities.</summary>
public sealed partial class UninstallResidueNode(string title, string fullPath, bool registry) : ObservableObject
{
    private bool _selected;
    private int _selectableCount;
    private int _selectedCount;
    private UninstallResidueNode? _parent;
    private Action<UninstallResidueNode>? _changed;
    private string _identity = title;
    public string Title { get; private set; } = title;
    public string FullPath { get; } = fullPath;
    public List<UninstallResidueNode> Children { get; } = [];
    public UninstallResidue? Finding { get; private set; }
    public Icon Icon => registry ? Icon.Database : Finding?.Kind == UninstallArtifactKind.File ? Icon.Document : Icon.Folder;
    public string Size => Finding is { Kind: UninstallArtifactKind.File } item ? $"{item.Bytes:N0} B" : string.Empty;
    public string Modified => Finding?.ModifiedUtc?.ToLocalTime().ToString("g") ?? string.Empty;
    public string Detail => Finding is { } finding ? $"{finding.Path}{(finding.ValueName != null ? "\\" + finding.ValueName : "")}\n{finding.Data}\n{finding.Reason}" : FullPath;
    public bool CanSelect => _selectableCount > 0;
    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;

    public bool? IsSelected
    {
        get => _selectedCount == 0 ? false : _selectedCount == _selectableCount ? true : null;
        set
        {
            if (!value.HasValue) return;
            SelectSubtree(value.Value);
            for (var parent = _parent; parent != null; parent = parent._parent) parent.RefreshCounts();
            _changed?.Invoke(this);
        }
    }

    private void SelectSubtree(bool selected)
    {
        _selected = Finding?.CanDelete == true && selected;
        foreach (var child in Children) child.SelectSubtree(selected);
        RefreshCounts();
    }

    private void RefreshCounts()
    {
        _selectableCount = (Finding?.CanDelete == true ? 1 : 0) + Children.Sum(child => child._selectableCount);
        _selectedCount = (_selected ? 1 : 0) + Children.Sum(child => child._selectedCount);
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(CanSelect));
    }

    private bool AllResourcesSelected => (Finding == null || Finding.CanDelete && _selected) && Children.All(child => child.AllResourcesSelected);

    /// <summary>Parents are selected for deletion only when every resource below them is selected.</summary>
    public IEnumerable<Guid> SelectedIds()
    {
        if (_selected && Finding is { } item &&
            (item.Kind is not (UninstallArtifactKind.Directory or UninstallArtifactKind.RegistryKey) || AllResourcesSelected)) yield return item.Id;
        foreach (var child in Children)
            foreach (var id in child.SelectedIds()) yield return id;
    }

    public static IReadOnlyList<UninstallResidueNode> Build(IEnumerable<UninstallResidue> findings, bool registry,
        string view32, string view64, Action<UninstallResidueNode> changed)
    {
        List<UninstallResidueNode> roots = [];
        foreach (var finding in findings)
        {
            var components = new List<string> { finding.AppName, finding.Account };
            if (registry) components.Add(finding.View32 ? view32 : view64);
            components.AddRange(finding.Path.Split('\\', StringSplitOptions.RemoveEmptyEntries));
            if (finding.Kind == UninstallArtifactKind.RegistryValue) components.Add(finding.ValueName is { Length: > 0 } name ? name : "(Default)");
            var siblings = roots;
            UninstallResidueNode? parent = null;
            var accumulated = string.Empty;
            for (var index = 0; index < components.Count; index++)
            {
                var component = components[index];
                accumulated = accumulated.Length == 0 ? component : accumulated + "\\" + component;
                var identity = finding.Kind == UninstallArtifactKind.RegistryValue && index == components.Count - 1
                    ? "value:" + finding.ValueName : "path:" + component;
                var node = siblings.FirstOrDefault(node => node._identity == identity);
                if (node == null)
                {
                    node = new(component, accumulated, registry) { _identity = identity, _parent = parent, _changed = changed, IsExpanded = index < 6 };
                    siblings.Add(node);
                }
                if (index == components.Count - 1)
                {
                    node.Finding = finding;
                    if (finding.Kind == UninstallArtifactKind.RegistryValue && finding.Data.Length > 0)
                        node.Title = component + " = " + finding.Data.Replace('\r', ' ').Replace('\n', ' ');
                }
                parent = node;
                siblings = node.Children;
            }
        }
        foreach (var root in roots) Initialize(root);
        return roots;

        static void Initialize(UninstallResidueNode node)
        {
            foreach (var child in node.Children) Initialize(child);
            node.RefreshCounts();
        }
    }
}
