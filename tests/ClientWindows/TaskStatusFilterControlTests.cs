using System.IO;
using System.Windows;
using Pim.Client.App;
using Xunit;
using CheckBox = System.Windows.Controls.CheckBox;

namespace Pim.Client.App.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class TaskStatusFilterControlTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void DefaultSelectionMeansAllStatuses()
    {
        wpf.Run(() =>
        {
            var filter = new TaskStatusFilterControl();
            var window = Host(filter);
            try
            {
                Assert.Empty(filter.SelectedStatuses);
                Assert.True(CheckBox(filter, "AllStatusCheck").IsChecked);
                Assert.All(
                    new[] { "NeedsActionCheck", "InProcessCheck", "CompletedCheck", "CancelledCheck" },
                    name => Assert.False(CheckBox(filter, name).IsChecked));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SelectingStatusesProducesAnOrSetAndAllStatusResetsIt()
    {
        wpf.Run(() =>
        {
            var filter = new TaskStatusFilterControl();
            var window = Host(filter);
            var changes = new List<IReadOnlyList<string>>();
            filter.SelectedStatusesChanged += (_, _) =>
                changes.Add(filter.SelectedStatuses.ToArray());

            try
            {
                Click(CheckBox(filter, "NeedsActionCheck"));
                Assert.Equal(["NEEDS-ACTION"], filter.SelectedStatuses);
                Assert.False(CheckBox(filter, "AllStatusCheck").IsChecked);

                Click(CheckBox(filter, "InProcessCheck"));
                Assert.Equal(["NEEDS-ACTION", "IN-PROCESS"], filter.SelectedStatuses);

                Click(CheckBox(filter, "NeedsActionCheck"));
                Assert.Equal(["IN-PROCESS"], filter.SelectedStatuses);

                Click(CheckBox(filter, "AllStatusCheck"));
                Assert.Empty(filter.SelectedStatuses);
                Assert.True(CheckBox(filter, "AllStatusCheck").IsChecked);
                Assert.All(
                    new[] { "NeedsActionCheck", "InProcessCheck", "CompletedCheck", "CancelledCheck" },
                    name => Assert.False(CheckBox(filter, name).IsChecked));

                Assert.Equal(
                    new[]
                    {
                        new[] { "NEEDS-ACTION" },
                        new[] { "NEEDS-ACTION", "IN-PROCESS" },
                        new[] { "IN-PROCESS" },
                        Array.Empty<string>(),
                    },
                    changes.Select(change => change.ToArray()).ToArray());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EveryContractStatusIsExposedWithSimplifiedChineseText()
    {
        wpf.Run(() =>
        {
            var filter = new TaskStatusFilterControl();
            var window = Host(filter);
            try
            {
                Assert.Equal("全部状态", CheckBox(filter, "AllStatusCheck").Content);
                Assert.Equal("待处理", CheckBox(filter, "NeedsActionCheck").Content);
                Assert.Equal("进行中", CheckBox(filter, "InProcessCheck").Content);
                Assert.Equal("已完成", CheckBox(filter, "CompletedCheck").Content);
                Assert.Equal("已取消", CheckBox(filter, "CancelledCheck").Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void NativePlannerUsesTheSharedMultiSelectControl()
    {
        var xaml = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "NativePlannerWindow.xaml"));
        var code = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "NativePlannerWindow.xaml.cs"));

        Assert.Contains("<local:TaskStatusFilterControl x:Name=\"TaskStatusFilter\"", xaml);
        Assert.Contains("SelectedStatusesChanged=\"OnTaskFilterChanged\"", xaml);
        Assert.DoesNotContain("TaskStatusFilterBox", xaml);
        Assert.Contains("TaskStatusFilter.SelectedStatuses", code);
        Assert.Contains("statuses: statuses", code);
    }

    private static Window Host(TaskStatusFilterControl filter)
    {
        var window = new Window
        {
            Content = filter,
            Width = 640,
            Height = 120,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static CheckBox CheckBox(TaskStatusFilterControl filter, string name)
        => Assert.IsType<CheckBox>(filter.FindName(name));

    private static void Click(CheckBox checkBox)
        => checkBox.IsChecked = checkBox.IsChecked != true;

    private static string RepoPath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(parts)}.");
    }
}
