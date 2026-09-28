using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using DiskAnalyzer.App.Views;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>
/// 정보 / 라이센스 / 업데이트 확인. GitHub Releases 의 최신 태그와 현재 버전을 비교할 뿐, 실행 중인
/// 프로그램을 스스로 종료·교체·재시작하지 않는다 — zip 을 내려받아 탐색기로 보여 주고 설치는 사용자가 한다.
/// </summary>
public sealed partial class MainViewModel
{
    private UpdateAsset? _updateZipAsset;
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
            _updateChecksumsAsset = release.FindChecksums();
            _latestReleaseUrl = release.HtmlUrl;
            LatestUpdateVersion = release.TagName;
            Raise(nameof(UpdateAvailableText));

            if (_updateZipAsset is null)
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
