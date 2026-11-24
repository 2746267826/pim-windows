using System.Windows;
using System.Windows.Controls;

namespace Pim.Client.App;

/// <summary>
/// R04: native multi-select task status entry. No checkbox means "all statuses",
/// which preserves the old single-value filter's default while allowing OR selections.
/// </summary>
public partial class TaskStatusFilterControl : UserControl
{
    private bool _updating;

    public TaskStatusFilterControl()
    {
        InitializeComponent();
        ApplySelection(Array.Empty<string>());
    }

    public event EventHandler? SelectedStatusesChanged;

    public IReadOnlyList<string> SelectedStatuses { get; private set; } = Array.Empty<string>();

    private (string Status, CheckBox Box)[] StatusBoxes =>
    [
        ("NEEDS-ACTION", NeedsActionCheck),
        ("IN-PROCESS", InProcessCheck),
        ("COMPLETED", CompletedCheck),
        ("CANCELLED", CancelledCheck),
    ];

    private void OnAllStatusChanged(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        // Clicking "全部状态" in either direction is a reset to the unfiltered state.
        ApplySelection(Array.Empty<string>());
    }

    private void OnStatusChanged(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        ApplySelection(
            StatusBoxes
                .Where(item => item.Box.IsChecked == true)
                .Select(item => item.Status)
                .ToArray());
    }

    private void ApplySelection(IReadOnlyList<string> selected)
    {
        var normalized = selected.Distinct(StringComparer.Ordinal).ToArray();
        _updating = true;
        try
        {
            foreach (var (status, box) in StatusBoxes)
            {
                box.IsChecked = normalized.Contains(status, StringComparer.Ordinal);
            }

            AllStatusCheck.IsChecked = normalized.Length == 0;
        }
        finally
        {
            _updating = false;
        }

        if (SelectedStatuses.SequenceEqual(normalized, StringComparer.Ordinal))
        {
            return;
        }

        SelectedStatuses = normalized;
        SelectedStatusesChanged?.Invoke(this, EventArgs.Empty);
    }
}
