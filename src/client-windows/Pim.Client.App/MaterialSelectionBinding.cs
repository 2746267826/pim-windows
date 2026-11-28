using System.Windows.Controls;
using Pim.Client.Core.Services.Notes;

namespace Pim.Client.App;

internal sealed class MaterialSelectionBinding
{
    private readonly ListBox _list;
    private readonly HandwritingMaterialSelectionState _selection;
    private Guid? _selectedMaterialId;
    private bool _syncingSelection;

    public MaterialSelectionBinding(ListBox list, HandwritingMaterialSelectionState selection)
    {
        _list = list;
        _selection = selection;
        _list.SelectionChanged += OnSelectionChanged;
    }

    public event EventHandler? SelectionChanged;

    public LocalNoteMaterial? SelectedMaterial => _list.SelectedItem as LocalNoteMaterial;

    public void Bind(IReadOnlyList<LocalNoteMaterial> materials)
    {
        var selectedMaterialId = SelectedMaterial?.MaterialId;
        SyncSelection(() =>
        {
            _list.ItemsSource = materials;
            _list.SelectedItem = selectedMaterialId is { } id
                ? materials.FirstOrDefault(material => material.MaterialId == id)
                : null;
        });
    }

    public bool Select(Guid materialId)
    {
        var material = _list.Items
            .OfType<LocalNoteMaterial>()
            .FirstOrDefault(item => item.MaterialId == materialId);
        if (material is null)
        {
            return false;
        }

        SyncSelection(() =>
        {
            _list.SelectedItem = material;
            _list.ScrollIntoView(material);
        });
        return SelectedMaterial?.MaterialId == materialId;
    }

    private void SyncSelection(Action action)
    {
        var previous = _selectedMaterialId;
        _syncingSelection = true;
        try
        {
            action();
        }
        finally
        {
            _selectedMaterialId = SelectedMaterial?.MaterialId;
            _syncingSelection = false;
            if (_selectedMaterialId != previous)
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedMaterialId = SelectedMaterial?.MaterialId;
        if (!_syncingSelection && selectedMaterialId != _selectedMaterialId)
        {
            _selection.MarkChanged();
        }

        _selectedMaterialId = selectedMaterialId;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
