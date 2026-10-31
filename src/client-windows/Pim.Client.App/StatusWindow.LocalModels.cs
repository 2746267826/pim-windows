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
    private CancellationTokenSource? _runtimeInstallCancellation;
    private IReadOnlyDictionary<string, LocalModelManifest> _manifestById =
        new Dictionary<string, LocalModelManifest>();
    private IReadOnlyDictionary<string, LocalRuntimeManifest> _runtimeManifestById =
        new Dictionary<string, LocalRuntimeManifest>();

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
            await _runtimeInstallStore.RecoverAsync(verifyInstalledHashes: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LocalModelStateText.Text = "恢复上次安装状态失败：" + ex.GetType().Name;
        }

        var catalogPath = LocalRuntimePaths.CatalogFile();
        if (!File.Exists(catalogPath))
        {
            _manifestById = new Dictionary<string, LocalModelManifest>();
            _runtimeManifestById = new Dictionary<string, LocalRuntimeManifest>();
            LocalModelsList.ItemsSource = null;
            LocalRuntimesList.ItemsSource = null;
            LocalModelStateText.Text =
                "未找到运行时目录 catalog.json（" + catalogPath + "）。" +
                "本机不会自动下载任何模型；由管理员放入目录后才会出现可安装条目。";
            LocalRuntimeStateText.Text = "未找到运行时目录；不会自动下载或执行任何运行时。";
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
            _runtimeManifestById = new Dictionary<string, LocalRuntimeManifest>();
            LocalModelsList.ItemsSource = null;
            LocalRuntimesList.ItemsSource = null;
            LocalModelStateText.Text = "读取运行时目录失败：" + ex.GetType().Name;
            LocalRuntimeStateText.Text = "读取运行时目录失败。";
            return;
        }

        _manifestById = parse.Catalog.Models.ToDictionary(model => model.ModelId, StringComparer.Ordinal);
        _runtimeManifestById = parse.Catalog.Runtimes.ToDictionary(
            runtime => runtime.RuntimeId,
            StringComparer.Ordinal);
        var accountId = _authService.CurrentUserId;
        var items = new ObservableCollection<LocalModelSectionItem>();
        var runtimeItems = new ObservableCollection<LocalRuntimeSectionItem>();
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

            foreach (var runtime in parse.Catalog.Runtimes)
            {
                runtimeItems.Add(new LocalRuntimeSectionItem(
                    runtime.RuntimeId,
                    runtime.DisplayName,
                    runtime.HasVerifiedRuntime,
                    Executable: false,
                    "未登录：登录后才能查看本账号的运行时安装状态。"));
            }
        }
        else
        {
            foreach (var model in parse.Catalog.Models)
            {
                var status = await _modelInstallStore.GetStatusAsync(accountId, model.ModelId);
                items.Add(LocalModelSectionView.ToItem(model, status));
            }

            foreach (var runtime in parse.Catalog.Runtimes)
            {
                var status = await _runtimeInstallStore.GetStatusAsync(accountId, runtime.RuntimeId);
                runtimeItems.Add(LocalModelSectionView.ToRuntimeItem(runtime, status));
            }
        }

        LocalModelsList.ItemsSource = items;
        LocalRuntimesList.ItemsSource = runtimeItems;
        LocalModelStateText.Text = items.Count == 0
            ? "运行时目录中没有模型条目。"
            : LocalModelSectionView.FormatCatalogProblem(parse.Errors) is { Length: > 0 } problem
                ? problem
                : $"已加载 {items.Count} 个模型条目。安装、校验、卸载均为手动操作。";
        LocalRuntimeStateText.Text = runtimeItems.Count == 0
            ? "运行时目录中没有可安装运行时。"
            : $"已加载 {runtimeItems.Count} 个运行时条目。归档解包和逐文件摘要校验均为手动操作。";
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

    private async void OnInstallRuntime(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedRuntimeManifest(out var manifest, out var error))
        {
            LocalRuntimeStateText.Text = error;
            return;
        }

        var accountId = _authService.CurrentUserId;
        if (accountId is null)
        {
            LocalRuntimeStateText.Text = "请先登录，再安装运行时。";
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"确认从 {manifest.SourceUrl} 下载并校验 {manifest.DisplayName}？\n" +
            $"归档约 {manifest.SizeBytes / (1024.0 * 1024):F1} MB；" +
            $"安装后必须匹配 {manifest.RequiredFiles.Count} 个 EXE/DLL 文件的大小与 SHA-256。",
            "安装本地运行时",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _runtimeInstallCancellation = new CancellationTokenSource();
        InstallRuntimeButton.IsEnabled = false;
        CancelRuntimeInstallButton.IsEnabled = true;
        try
        {
            var progress = new Progress<RuntimeInstallProgress>(p =>
                LocalRuntimeStateText.Text =
                    $"{p.State} · {p.BytesReceived / (1024.0 * 1024):F1} / " +
                    $"{p.ExpectedBytes / (1024.0 * 1024):F1} MB");
            var status = await _runtimeInstallStore.InstallAsync(
                manifest,
                accountId,
                progress,
                _runtimeInstallCancellation.Token);
            LocalRuntimeStateText.Text = LocalModelSectionView.FormatRuntimeStatus(manifest, status);
        }
        catch (Exception ex)
        {
            LocalRuntimeStateText.Text = "运行时安装失败：" + ex.GetType().Name;
        }
        finally
        {
            InstallRuntimeButton.IsEnabled = true;
            CancelRuntimeInstallButton.IsEnabled = false;
            _runtimeInstallCancellation.Dispose();
            _runtimeInstallCancellation = null;
            await RefreshLocalModelsAsync();
        }
    }

    private void OnCancelRuntimeInstall(object sender, RoutedEventArgs e)
    {
        _runtimeInstallCancellation?.Cancel();
        LocalRuntimeStateText.Text = "正在取消运行时安装…";
    }

    private async void OnVerifyRuntime(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedRuntimeManifest(out var manifest, out var error))
        {
            LocalRuntimeStateText.Text = error;
            return;
        }

        var accountId = _authService.CurrentUserId;
        if (accountId is null)
        {
            LocalRuntimeStateText.Text = "请先登录，再校验运行时。";
            return;
        }

        try
        {
            LocalRuntimeStateText.Text = "正在校验运行时归档内每个 EXE/DLL…";
            var status = await _runtimeInstallStore.VerifyAsync(manifest, accountId);
            LocalRuntimeStateText.Text = LocalModelSectionView.FormatRuntimeStatus(manifest, status);
            await RefreshLocalModelsAsync();
        }
        catch (Exception ex)
        {
            LocalRuntimeStateText.Text = "运行时校验失败：" + ex.GetType().Name;
        }
    }

    private async void OnUninstallRuntime(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedRuntimeManifest(out var manifest, out var error))
        {
            LocalRuntimeStateText.Text = error;
            return;
        }

        var accountId = _authService.CurrentUserId;
        if (accountId is null)
        {
            LocalRuntimeStateText.Text = "请先登录，再卸载运行时。";
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"卸载 {manifest.DisplayName} 并删除本账号的运行时文件？\n先确认本账号没有正在运行的任务。",
            "卸载本地运行时",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var removed = await _runtimeInstallStore.UninstallAsync(
                accountId,
                manifest.RuntimeId);
            LocalRuntimeStateText.Text = removed
                ? $"已卸载 {manifest.DisplayName}。"
                : "未找到可卸载的运行时。";
            await RefreshLocalModelsAsync();
        }
        catch (Exception ex)
        {
            LocalRuntimeStateText.Text = "运行时卸载失败：" + ex.GetType().Name;
        }
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

    private bool TryGetSelectedRuntimeManifest(
        out LocalRuntimeManifest manifest,
        out string error)
    {
        if (LocalRuntimesList.SelectedItem is not LocalRuntimeSectionItem item)
        {
            manifest = null!;
            error = "请先选择一个运行时条目。";
            return false;
        }

        if (!_runtimeManifestById.TryGetValue(item.RuntimeId, out var found))
        {
            manifest = null!;
            error = "运行时条目已过期，请刷新后重试。";
            return false;
        }

        if (!found.HasVerifiedRuntime)
        {
            manifest = null!;
            error = "该运行时没有登记归档、逐文件摘要、执行参数或修订，禁止安装。";
            return false;
        }

        manifest = found;
        error = string.Empty;
        return true;
    }
}
