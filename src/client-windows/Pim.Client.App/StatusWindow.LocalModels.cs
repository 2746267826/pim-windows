using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services.LocalRuntime;
using Pim.Client.App.Services;

namespace Pim.Client.App;

/// <summary>
/// 本地模型 section of the status window (issue #516 phase 3 slice). Everything here
/// is user-initiated: the app never starts a download or a runtime on its own.
/// </summary>
public partial class StatusWindow
{
    private CancellationTokenSource? _installCancellation;
    private IReadOnlyDictionary<string, LocalModelManifest> _manifestById =
        new Dictionary<string, LocalModelManifest>();

    private async void OnLocalModelsRefresh(object sender, RoutedEventArgs e)
    {
        await RefreshLocalModelsAsync().ContinueWith(
            task => Debug.WriteLine(task.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task RefreshLocalModelsAsync()
    {
        LocalModelStateText.Text = "正在读取本地运行时目录…";
        try
        {
            await _modelInstallStore.RecoverAsync(verifyInstalledHashes: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LocalModelStateText.Text = "恢复上次安装状态失败：" + ex.GetType().Name;
        }

        var catalogPath = LocalRuntimePaths.CatalogFile();
        if (!File.Exists(catalogPath))
        {
            _manifestById = new Dictionary<string, LocalModelManifest>();
            LocalModelsList.ItemsSource = null;
            LocalModelStateText.Text =
                "未找到运行时目录 catalog.json（" + catalogPath + "）。" +
                "本机不会自动下载任何模型；由管理员放入目录后才会出现可安装条目。";
            return;
        }

        LocalRuntimeCatalogParseResult parse;
        try
        {
            parse = LocalRuntimeCatalogParser.Parse(
                await File.ReadAllTextAsync(catalogPath, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _manifestById = new Dictionary<string, LocalModelManifest>();
            LocalModelsList.ItemsSource = null;
            LocalModelStateText.Text = "读取运行时目录失败：" + ex.GetType().Name;
            return;
        }

        _manifestById = parse.Catalog.Models.ToDictionary(model => model.ModelId, StringComparer.Ordinal);
        var accountId = _authService.CurrentUserId;
        var items = new ObservableCollection<LocalModelSectionItem>();
        if (accountId is null)
        {
            foreach (var model in parse.Catalog.Models)
            {
                items.Add(new LocalModelSectionItem(
                    model.ModelId,
                    model.DisplayName,
                    model.HasVerifiedArtifact,
                    Executable: false,
                    "未登录：登录后才能查看本账号的模型安装状态。"));
            }
        }
        else
        {
            foreach (var model in parse.Catalog.Models)
            {
                var status = await _modelInstallStore.GetStatusAsync(accountId, model.ModelId);
                items.Add(LocalModelSectionView.ToItem(model, status));
            }
        }

        LocalModelsList.ItemsSource = items;
        LocalModelStateText.Text = items.Count == 0
            ? "运行时目录为空。"
            : LocalModelSectionView.FormatCatalogProblem(parse.Errors) is { Length: > 0 } problem
                ? problem
                : $"已加载 {items.Count} 个模型条目。安装、校验、卸载均为手动操作。";
    }

    private async void OnInstallModel(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedManifest(out var manifest, out var error))
        {
            var accountId = _authService.CurrentUserId;
            if (accountId is null)
            {
                LocalModelStateText.Text = "请先登录，再安装模型。";
                return;
            }

            var confirmation = MessageBox.Show(
                this,
                $"确认从 {manifest.SourceUrl} 下载并校验 {manifest.DisplayName}？" +
                $"（约 {manifest.SizeBytes / (1024.0 * 1024):F1} MB，需要磁盘空间，下载与校验均可取消）",
                "安装本地模型",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            _installCancellation = new CancellationTokenSource();
            InstallModelButton.IsEnabled = false;
            CancelInstallButton.IsEnabled = true;
            try
            {
                var progress = new Progress<ModelInstallProgress>(p =>
                    LocalModelStateText.Text =
                        $"下载 {p.BytesReceived / (1024.0 * 1024):F1} / {p.ExpectedBytes / (1024.0 * 1024):F1} MB（{p.State}）");
                var status = await _modelInstallStore.InstallAsync(
                    manifest,
                    accountId,
                    progress,
                    _installCancellation.Token);
                LocalModelStateText.Text = LocalModelSectionView.FormatStatus(manifest, status);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LocalModelStateText.Text = "安装失败：" + ex.GetType().Name;
            }
            catch (Exception ex)
            {
                LocalModelStateText.Text = "安装失败：" + ex.GetType().Name;
            }
            finally
            {
                InstallModelButton.IsEnabled = true;
                CancelInstallButton.IsEnabled = false;
                _installCancellation.Dispose();
                _installCancellation = null;
                await RefreshLocalModelsAsync();
            }
        }
        else
        {
            LocalModelStateText.Text = error;
        }
    }

    private void OnCancelInstall(object sender, RoutedEventArgs e)
    {
        _installCancellation?.Cancel();
        LocalModelStateText.Text = "正在取消安装…";
    }

    private async void OnVerifyModel(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedManifest(out var manifest, out var error))
        {
            var accountId = _authService.CurrentUserId;
            if (accountId is null)
            {
                LocalModelStateText.Text = "请先登录，再校验模型。";
                return;
            }

            try
            {
                LocalModelStateText.Text = "正在校验摘要…";
                var status = await _modelInstallStore.VerifyAsync(accountId, manifest.ModelId);
                LocalModelStateText.Text = LocalModelSectionView.FormatStatus(manifest, status);
                await RefreshLocalModelsAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LocalModelStateText.Text = "校验失败：" + ex.GetType().Name;
            }
        }
        else
        {
            LocalModelStateText.Text = error;
        }
    }

    private async void OnUninstallModel(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedManifest(out var manifest, out var error))
        {
            var accountId = _authService.CurrentUserId;
            if (accountId is null)
            {
                LocalModelStateText.Text = "请先登录，再卸载模型。";
                return;
            }

            var confirmation = MessageBox.Show(
                this,
                $"卸载 {manifest.DisplayName} 并删除本账号的模型文件？",
                "卸载本地模型",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var removed = await _modelInstallStore.UninstallAsync(accountId, manifest.ModelId);
                LocalModelStateText.Text = removed
                    ? $"已卸载 {manifest.DisplayName}。"
                    : "未找到可卸载的安装。";
                await RefreshLocalModelsAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LocalModelStateText.Text = "卸载失败：" + ex.GetType().Name;
            }
        }
        else
        {
            LocalModelStateText.Text = error;
        }
    }

    private bool TryGetSelectedManifest(out LocalModelManifest manifest, out string error)
    {
        if (LocalModelsList.SelectedItem is not LocalModelSectionItem item)
        {
            manifest = null!;
            error = "请先选择一个模型条目。";
            return false;
        }

        if (!_manifestById.TryGetValue(item.ModelId, out var found))
        {
            manifest = null!;
            error = "模型条目已过期，请刷新后重试。";
            return false;
        }

        if (!found.HasVerifiedArtifact)
        {
            manifest = null!;
            error = "该条目没有登记摘要/修订/许可，禁止安装。";
            return false;
        }

        manifest = found;
        error = string.Empty;
        return true;
    }
}
