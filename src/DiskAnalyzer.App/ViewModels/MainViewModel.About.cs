using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using DiskAnalyzer.App.Views;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>
/// 정보 / 라이센스 / 업데이트. GitHub Releases 의 최신 태그와 현재 버전을 비교한다.
/// 릴리스에 DiskAnalyzer.exe 와 SHA256SUMS.txt 가 있고 실행 폴더에 쓸 수 있으면 exe 를 내려받아 검증한 뒤
/// <see cref="SelfUpdater"/> 로 종료 → 파일 교체 → 재시작한다. 아니면 zip 을 내려받아 탐색기로 보여 주고 설치는 사용자가 한다.
/// </summary>
public sealed partial class MainViewModel
{
    private UpdateAsset? _updateZipAsset;
    private UpdateAsset? _updateExeAsset;
    private UpdateAsset? _updateChecksumsAsset;
    private string _latestReleaseUrl = "";

    private bool _isCheckingUpdate;
    private bool _updateAvailable;
    private bool _isDownloadingUpdate;
    private string _updateStatusText = "";
    private string _latestUpdateVersion = "";
    private double _updateDownloadProgress;

    public string InternalVersion { get; } = typeof(MainViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? "0.0.0-internal";

    public bool IsCheckingUpdate { get => _isCheckingUpdate; private set => Set(ref _isCheckingUpdate, value); }
    public bool UpdateAvailable { get => _updateAvailable; private set => Set(ref _updateAvailable, value); }
    public bool IsDownloadingUpdate { get => _isDownloadingUpdate; private set => Set(ref _isDownloadingUpdate, value); }
    public string UpdateStatusText { get => _updateStatusText; private set => Set(ref _updateStatusText, value); }
    public string LatestUpdateVersion { get => _latestUpdateVersion; private set => Set(ref _latestUpdateVersion, value); }
    /// <summary>제자리 교체가 가능하면 "지금 업데이트"(앱이 잠시 종료됐다 다시 열린다), 아니면 "업데이트 내려받기".</summary>
    public string UpdateButtonText => CanInstallInPlace ? "지금 업데이트 (자동 재시작)" : "업데이트 내려받기";
    private bool CanInstallInPlace => _updateExeAsset is not null && _updateChecksumsAsset is not null && SelfUpdater.CanReplaceInPlace();
    public string UpdateAvailableText => $"새 버전 {LatestUpdateVersion} 을(를) 사용할 수 있습니다.";

    public double UpdateDownloadProgress
    {
        get => _updateDownloadProgress;
        private set { Set(ref _updateDownloadProgress, value); Raise(nameof(UpdateDownloadProgressText)); }
    }
    public string UpdateDownloadProgressText => $"다운로드 중... {(int)Math.Round(UpdateDownloadProgress * 100)}%";

    public ICommand CheckForUpdateCommand { get; private set; } = null!;
    public ICommand DownloadUpdateCommand { get; private set; } = null!;
    public ICommand OpenReleaseNotesCommand { get; private set; } = null!;
    public ICommand OpenLicenseCommand { get; private set; } = null!;
    public ICommand OpenOpenSourceLicensesCommand { get; private set; } = null!;

    private void InitAboutCommands()
    {
        CheckForUpdateCommand = new RelayCommand(() => _ = CheckForUpdateAsync(manual: true));
        DownloadUpdateCommand = new RelayCommand(() => _ = DownloadUpdateAsync());
        OpenReleaseNotesCommand = new RelayCommand(() => OpenUrl(_latestReleaseUrl));
        OpenLicenseCommand = new RelayCommand(() => ShowLegalDocument("LICENSE", "라이센스", "DiskAnalyzer 사용 조건"));
        OpenOpenSourceLicensesCommand = new RelayCommand(() => ShowLegalDocument(
            "THIRD-PARTY-NOTICES.md", "오픈소스 라이선스", "이 프로그램이 사용하는 third-party 구성 요소"));

        _ = CheckForUpdateAsync(manual: false);
    }

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (IsCheckingUpdate || IsDownloadingUpdate) return;
        IsCheckingUpdate = true;
        if (manual) UpdateStatusText = "업데이트 확인 중...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var release = await UpdateChecker.FetchLatestAsync(cts.Token);
            if (release is null)
            {
                if (manual) UpdateStatusText = "업데이트 확인에 실패했습니다.";
                return;
            }

            var currentVersion = InternalVersion.Replace("-internal", "", StringComparison.OrdinalIgnoreCase);
            if (!UpdateChecker.IsNewer(release.TagName, currentVersion))
            {
                UpdateAvailable = false;
                if (manual) UpdateStatusText = "최신 버전을 사용 중입니다.";
                return;
            }

            _updateZipAsset = release.FindZip();
            _updateExeAsset = release.FindExe(SelfUpdater.ExeAssetName);
            _updateChecksumsAsset = release.FindChecksums();
            _latestReleaseUrl = release.HtmlUrl;
            LatestUpdateVersion = release.TagName;
            Raise(nameof(UpdateAvailableText));
            Raise(nameof(UpdateButtonText));

            if (_updateZipAsset is null && _updateExeAsset is null)
            {
                UpdateAvailable = false;
                if (manual) UpdateStatusText = "새 버전이 있지만 내려받을 파일이 없습니다.";
                return;
            }

            UpdateAvailable = true;
            UpdateStatusText = "";
            if (!manual) StatusMessage = $"새 버전 {release.TagName} 을(를) 사용할 수 있습니다. 설정에서 내려받으세요.";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private async Task DownloadUpdateAsync()
    {
        if (CanInstallInPlace)
        {
            await InstallUpdateAsync(_updateExeAsset!, _updateChecksumsAsset!);
            return;
        }

        if (_updateZipAsset is null || IsDownloadingUpdate) return;
        IsDownloadingUpdate = true;
        UpdateDownloadProgress = 0;
        UpdateStatusText = "";
        try
        {
            var destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var progress = new Progress<double>(p => UpdateDownloadProgress = p);
            var path = await UpdateChecker.DownloadAssetAsync(_updateZipAsset, destination, progress, cts.Token);

            if (_updateChecksumsAsset is not null)
            {
                var sums = await UpdateChecker.FetchTextAsync(_updateChecksumsAsset.BrowserDownloadUrl, cts.Token);
                if (sums is not null && !UpdateChecker.VerifyChecksum(sums, _updateZipAsset.Name, path))
                {
                    File.Delete(path);
                    UpdateStatusText = "체크섬 검증에 실패했습니다. 다시 시도해 주세요.";
                    return;
                }
            }

            UpdateStatusText = "다운로드를 마쳤습니다.";
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UpdateStatusText = "다운로드 실패: " + ex.Message;
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    /// <summary>exe 를 내려받고 SHA256SUMS.txt 로 검증(항목 필수)한 뒤, 도우미에게 넘기고 앱을 끝낸다. 도우미가 파일을 바꾸고 다시 연다.</summary>
    private async Task InstallUpdateAsync(UpdateAsset exeAsset, UpdateAsset checksumsAsset)
    {
        if (IsDownloadingUpdate) return;
        IsDownloadingUpdate = true;
        UpdateDownloadProgress = 0;
        UpdateStatusText = "";
        bool exit = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var progress = new Progress<double>(p => UpdateDownloadProgress = p);
            var path = await UpdateChecker.DownloadAssetAsync(exeAsset, SelfUpdater.UpdateDirectory, progress, cts.Token);

            var sums = await UpdateChecker.FetchTextAsync(checksumsAsset.BrowserDownloadUrl, cts.Token);
            if (sums is null || !UpdateChecker.VerifyChecksum(sums, exeAsset.Name, path, requireEntry: true))
            {
                File.Delete(path);
                UpdateStatusText = "체크섬 검증에 실패했습니다. 다시 시도해 주세요.";
                return;
            }

            UpdateStatusText = "업데이트를 적용하는 중... 앱이 잠시 종료됐다가 다시 열립니다.";
            if (!SelfUpdater.LaunchHelper(path))
            {
                UpdateStatusText = "업데이트 도우미를 실행하지 못했습니다.";
                return;
            }
            exit = true;
        }
        catch (Exception ex)
        {
            UpdateStatusText = "다운로드 실패: " + ex.Message;
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
        if (exit) Application.Current.Shutdown();
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private static void ShowLegalDocument(string fileName, string title, string subtitle)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        var body = File.Exists(path) ? File.ReadAllText(path) : $"{fileName} 파일을 찾을 수 없습니다.";
        var window = new LegalTextWindow(title, subtitle, body)
        {
            Owner = Application.Current.MainWindow?.IsVisible == true ? Application.Current.MainWindow : null,
        };
        window.ShowDialog();
    }
}
