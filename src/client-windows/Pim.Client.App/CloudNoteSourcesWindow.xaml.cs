using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services.Notes;

namespace Pim.Client.App;

public partial class CloudNoteSourcesWindow : Window
{
    private readonly CloudNoteMaterialService _cloud;

    public CloudNoteSourcesWindow()
    {
        InitializeComponent();
        _cloud = App.Services.GetRequiredService<CloudNoteMaterialService>();
    }

    public Guid? SelectedNoteId { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private async void OnRefresh(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private async Task RefreshAsync()
    {
        StatusText.Text = "正在读取服务器目录…";
        try
        {
            var sources = await _cloud.GetMaterialSourcesAsync();
            var rows = sources
                .GroupBy(source => source.NoteId, StringComparer.Ordinal)
                .Select(group =>
                {
                    var items = group.OrderByDescending(source => source.CreatedAt).ToArray();
                    var representative = items.FirstOrDefault(source => source.IsCurrent) ?? items[0];
                    return new CloudSourceRow(
                        representative,
                        items.Length,
                        items.Count(source => source.HasConflict),
                        Label(representative, items.Length, items.Count(source => source.HasConflict)));
                })
                .OrderByDescending(row => row.Source.CreatedAt)
                .ToArray();
            SourcesList.ItemsSource = rows;
            SourcesList.SelectedItem = rows.FirstOrDefault();
            StatusText.Text = rows.Length == 0
                ? "服务器尚无已完成材料版本。"
                : $"服务器共有 {rows.Length} 篇笔记、{sources.Count} 个已完成材料版本。";
        }
        catch (Exception ex)
        {
            SourcesList.ItemsSource = null;
            StatusText.Text = "服务器目录暂不可用；没有修改本机笔记。";
            MessageBox.Show(
                this,
                ex.Message,
                "读取服务器目录失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnSourceDoubleClick(object sender, MouseButtonEventArgs e)
        => SelectCurrent();

    private void OnSelect(object sender, RoutedEventArgs e)
        => SelectCurrent();

    private void SelectCurrent()
    {
        if (SourcesList.SelectedItem is not CloudSourceRow row)
        {
            return;
        }

        if (!Guid.TryParse(row.Source.NoteId, out var noteId))
        {
            MessageBox.Show(
                this,
                "服务器返回的笔记标识无效；没有切换本机笔记。",
                "服务器目录",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SelectedNoteId = noteId;
        DialogResult = true;
    }

    private static string Label(NoteAsrSourceDto source, int versions, int conflicts)
    {
        var state = source.IsCurrent ? "[当前]" : conflicts > 0 ? "[有冲突]" : "[历史]";
        return $"{state} {source.Title} · {versions} 个版本 · {source.Recordings} 段录音 · "
            + $"{Duration(source.TotalSamples, source.SampleRate)} · {source.CreatedAt.ToLocalTime():MM-dd HH:mm}";
    }

    private static string Duration(long samples, int sampleRate)
    {
        if (sampleRate <= 0)
        {
            return "00:00:00";
        }

        var seconds = Math.Max(0, samples) / (double)sampleRate;
        if (!double.IsFinite(seconds) || seconds >= TimeSpan.MaxValue.TotalSeconds)
        {
            return "超出范围";
        }

        return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");
    }

    private sealed record CloudSourceRow(
        NoteAsrSourceDto Source,
        int VersionCount,
        int ConflictCount,
        string Label);
}
