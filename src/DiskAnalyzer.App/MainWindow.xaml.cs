using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskAnalyzer.App.Controls;
using DiskAnalyzer.App.ViewModels;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Scanning;
using DiskAnalyzer.Core.Services;
using Microsoft.Win32;

namespace DiskAnalyzer.App;

/// <summary>
/// 19. UI Layer.
/// 여기에는 파일 시스템 접근 코드가 없다. 사용자 입력을 ViewModel / Service 호출로 옮기는 역할만 한다.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        ElevationHint.Text = _vm.IsElevated
            ? "관리자 권한 - NTFS Fast Scan 사용 가능"
            : "일반 권한 - Compatibility Scan 으로 동작합니다. Fast Scan 을 쓰려면";

        _vm.ThemeChangeRequested += (_, theme) =>
        {
            App.ApplyTheme(theme switch
            {
                "Light" => App.AppTheme.Light,
                "시스템" => App.AppTheme.System,
                _ => App.AppTheme.Dark,
            });
            ApplyDarkTitleBar();
        };

        _vm.ViewRefreshed += (_, _) => UpdateTreemap();

        // [검색] 버튼과 Enter 가 같은 경로를 타도록 탭 전환은 여기서 한다. 결과는 폴더 목록에 나온다.
        _vm.SearchStarted += (_, _) => { if (Tabs.SelectedIndex != 0) Tabs.SelectedIndex = 0; };

        Treemap.HoverChanged += OnTreemapHover;
        Treemap.ItemActivated += OnTreemapActivated;
        Treemap.ItemSelected += (_, item) => TreemapInfo.Text = Describe(item);

        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += OnLoadedStartupScan;
    }

    /// <summary>
    /// DiskAnalyzer.exe "C:\" [--tab 0~4]
    /// 경로를 주면 실행 즉시 스캔하고, --tab 으로 시작 탭을 지정한다(벤치/자동화용).
    /// </summary>
    private void OnLoadedStartupScan(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedStartupScan;
        var args = Environment.GetCommandLineArgs();

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--tab", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int tab) && tab >= 0 && tab < Tabs.Items.Count)
                    Tabs.SelectedIndex = tab;
            }
            else if (i == 1 && !string.IsNullOrWhiteSpace(args[i]))
            {
                _vm.StartScan(args[i]);
            }
        }
    }

    /// <summary>
    /// 28. 관리자 권한으로 다시 실행.
    ///
    /// 프로세스는 자기 권한을 올릴 수 없다. UAC 승인을 받아 <strong>새 프로세스를 띄우고 현재 창을 닫는</strong> 것이
    /// Windows 에서 유일한 방법이다. 현재 스캔 경로와 탭을 인자로 넘겨 승격 후 같은 화면으로 돌아오게 한다.
    /// </summary>
    private void OnRestartAsAdmin(object sender, RoutedEventArgs e)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            _vm.StatusMessage = "실행 파일 경로를 확인할 수 없어 재실행하지 못했습니다.";
            return;
        }

        string? root = _vm.CurrentResult?.RootPath ?? _vm.SelectedDrive?.RootPath;

        var message = new System.Text.StringBuilder();
        message.AppendLine("관리자 권한으로 다시 실행합니다.");
        message.AppendLine("UAC 승인 창이 뜨고, 현재 창은 닫힙니다.");
        if (!string.IsNullOrWhiteSpace(root))
            message.AppendLine($"\n승격 후 {root} 를 다시 스캔합니다.");
        message.AppendLine("\n관리자 권한에서는 드라이브 루트 스캔에 NTFS Fast Scan 을 시도합니다.");

        if (MessageBox.Show(this, message.ToString(), "관리자 권한으로 다시 실행",
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) != MessageBoxResult.OK)
            return;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,   // runas 는 ShellExecute 로만 동작한다
                Verb = "runas",
            };
            if (!string.IsNullOrWhiteSpace(root)) psi.ArgumentList.Add(root);
            psi.ArgumentList.Add("--tab");
            psi.ArgumentList.Add(Tabs.SelectedIndex.ToString());

            System.Diagnostics.Process.Start(psi);
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED - 사용자가 UAC 를 거부했다. 지금 창은 그대로 쓸 수 있다.
            _vm.StatusMessage = "관리자 권한 요청이 취소되었습니다. 일반 권한으로 계속 사용합니다.";
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = "관리자 권한 재실행 실패: " + ex.Message;
        }
    }

    /// <summary>17. Windows 11 다크 모드에서 타이틀 바까지 어둡게 맞춘다.</summary>
    private void ApplyDarkTitleBar()
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int useDark = App.CurrentTheme == App.AppTheme.Light ? 0 : 1;
            DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int));
        }
        catch { /* 구버전 Windows 에서는 무시 */ }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---------------------------------------------------------------- 탭

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.OriginalSource != Tabs) return;

        var tab = (LiveTab)Tabs.SelectedIndex;
        FolderPanel.Visibility = tab == LiveTab.Folder ? Visibility.Visible : Visibility.Collapsed;
        LargeFilesPanel.Visibility = tab == LiveTab.LargeFiles ? Visibility.Visible : Visibility.Collapsed;
        FileTypesPanel.Visibility = tab == LiveTab.FileTypes ? Visibility.Visible : Visibility.Collapsed;
        TreemapPanel.Visibility = tab == LiveTab.Treemap ? Visibility.Visible : Visibility.Collapsed;
        CleanupPanel.Visibility = tab == LiveTab.Cleanup ? Visibility.Visible : Visibility.Collapsed;

        _vm.ActiveTab = tab;
        if (tab == LiveTab.Treemap) UpdateTreemap();
    }

    private void UpdateTreemap()
    {
        if (TreemapPanel.Visibility != Visibility.Visible) return;

        // 스캔 중에는 NodeStore 를 UI 스레드가 직접 타고 내려가면 안 된다(Aggregator 가 쓰는 중).
        if (_vm.IsScanning || _vm.CurrentResult == null) Treemap.SetFlatSource(_vm.Rows);
        else Treemap.SetSource(_vm.CurrentResult.Store, _vm.CurrentDirectoryId);
    }

    private void OnTreemapHover(object? sender, TreemapControl.TreemapItem? item)
        => TreemapInfo.Text = item == null
            ? "사각형 위에 마우스를 올리면 정보가 표시됩니다. 더블 클릭하면 해당 폴더로 이동합니다."
            : Describe(item);

    private static string Describe(TreemapControl.TreemapItem item)
    {
        string kind = item.IsDirectory ? "폴더" : (item.Extension.Length > 0 ? item.Extension : "파일");
        string tag = Categorizer.ToTagString(item.Category);
        return $"{item.Name}   |   {SizeFormatter.Format(item.Size)}   |   {kind}   |   {item.FullPath}" +
               (tag.Length > 0 ? "   " + tag : string.Empty);
    }

    /// <summary>
    /// Treemap 에서 폴더를 더블 클릭하면 <strong>폴더 탭으로 이동해 그 폴더를 연다</strong>.
    /// Treemap 은 "어디가 큰지"를 찾는 화면이고, 실제로 무엇이 들어 있는지는 폴더 탭에서 본다.
    /// 탭을 먼저 바꾸고 이동해야 폴더 탭이 곧바로 대상 폴더를 그린다.
    /// </summary>
    private void OnTreemapActivated(object? sender, TreemapControl.TreemapItem item)
    {
        if (item.IsDirectory)
        {
            Tabs.SelectedIndex = (int)LiveTab.Folder;
            _vm.Navigate(item.Id);
            FolderList.SelectedItems.Clear();
        }
        else
        {
            ShellService.OpenInExplorer(item.FullPath, false);
        }
    }

    // ---------------------------------------------------------------- 탐색

    private void OnDriveCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DriveInfoRow drive) return;
        _vm.SelectedDrive = drive;
        if (e.ClickCount >= 2) _vm.StartScan(drive.RootPath);
    }

    private void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int id }) _vm.Navigate(id);
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView list || list.SelectedItem is not EntryRow row) return;

        if (row.IsDirectory)
        {
            if (Tabs.SelectedIndex != 0) Tabs.SelectedIndex = 0;
            _vm.Navigate(row.Id);
        }
        else
        {
            ShellService.OpenInExplorer(row.FullPath, false);
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _vm.SearchCommand.Execute(null);
    }

    // ---------------------------------------------------------------- 정렬

    /// <summary>
    /// 컬럼 헤더 클릭 정렬. 정렬은 ViewModel 이 맡는다(RowSorter 주석 참고).
    /// 여기서 ListView.ItemsSource 를 직접 바꾸면 XAML 바인딩이 끊겨서 이후 검색/폴더 이동이 화면에 반영되지 않는다.
    /// </summary>
    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string key }) return;

        SortTarget? target = ReferenceEquals(sender, FolderList) ? SortTarget.Folder
            : ReferenceEquals(sender, LargeFileList) ? SortTarget.LargeFiles
            : ReferenceEquals(sender, ExtensionList) ? SortTarget.Extensions
            : ReferenceEquals(sender, ExtensionFileList) ? SortTarget.ExtensionFiles
            : null;
        if (target is { } t) _vm.ToggleSort(t, key);
    }

    // ---------------------------------------------------------------- 15. 우클릭 메뉴

    private static EntryRow? RowOf(object sender)
    {
        if (sender is not MenuItem item) return null;
        var menu = ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu
                   ?? item.Parent as ContextMenu;
        return (menu?.PlacementTarget as ListView)?.SelectedItem as EntryRow;
    }

    private void OnOpenExplorer(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) ShellService.OpenInExplorer(row.FullPath, row.IsDirectory);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) TrySetClipboard(row.FullPath);
    }

    private void OnCopyName(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) TrySetClipboard(row.Name);
    }

    private void OnShowProperties(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) ShellService.ShowProperties(row.FullPath);
    }

    private void OnRecycle(object sender, RoutedEventArgs e) => Delete(RowOf(sender), permanent: false);

    private void OnDeletePermanently(object sender, RoutedEventArgs e) => Delete(RowOf(sender), permanent: true);

    /// <summary>
    /// 16. 삭제 안전성.
    ///  - 프로그램이 스스로 "안전하다"고 판단하지 않는다. 항상 확인창을 띄운다.
    ///  - 시스템 영역(Windows / Program Files / ProgramData / WinSxS 등)은 별도 경고를 추가한다.
    ///  - 기본 동작은 휴지통이며 영구 삭제는 메뉴를 따로 선택해야 한다.
    /// </summary>
    private void Delete(EntryRow? row, bool permanent)
    {
        if (row == null) return;

        bool protectedPath = Categorizer.IsProtectedPath(row.FullPath);
        string warning = protectedPath
            ? "\n\n⚠ 이 경로는 Windows 시스템 영역입니다.\n삭제하면 시스템이 정상 동작하지 않을 수 있습니다."
            : string.Empty;

        string action = permanent ? "영구 삭제" : "휴지통으로 이동";
        var answer = MessageBox.Show(this,
            $"{action} 하시겠습니까?\n\n{row.FullPath}\n크기: {row.SizeText}{warning}",
            $"{action} 확인",
            MessageBoxButton.OKCancel,
            protectedPath ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK) return;

        if (protectedPath && permanent)
        {
            var second = MessageBox.Show(this,
                "시스템 영역을 영구 삭제하려고 합니다. 정말 진행하시겠습니까?",
                "한 번 더 확인", MessageBoxButton.YesNo, MessageBoxImage.Stop, MessageBoxResult.No);
            if (second != MessageBoxResult.Yes) return;
        }

        bool ok = permanent
            ? ShellService.DeletePermanently(row.FullPath)
            : ShellService.MoveToRecycleBin(row.FullPath);

        _vm.StatusMessage = ok
            ? $"{action} 완료: {row.FullPath} (결과 반영은 새로고침 후)"
            : $"{action} 실패: {row.FullPath}";
    }

    private void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            _vm.StatusMessage = "복사했습니다: " + text;
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = "클립보드 복사 실패: " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- 6/7. 큰 파일 다중 선택 / 삭제

    /// <summary>6. Ctrl+클릭 / Shift+클릭 / Ctrl+A 는 ListView 의 Extended 모드가 그대로 처리한다.</summary>
    private void OnLargeFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView list) return;

        int count = 0;
        long bytes = 0;
        foreach (var item in list.SelectedItems)
        {
            if (item is not EntryRow row) continue;
            count++;
            bytes += row.Size;
        }

        LargeFileSelectionText.Text = $"{count:N0}개 파일 선택됨";
        LargeFileSelectionSize.Text = SizeFormatter.Format(bytes);
    }

    private void OnClearLargeFileSelection(object sender, RoutedEventArgs e)
        => LargeFileList.SelectedItems.Clear();

    /// <summary>
    /// 7. 여러 파일을 선택해 삭제한다.
    /// 버튼을 눌러도 즉시 지우지 않고 반드시 삭제 대상 확인 창을 먼저 띄운다.
    /// </summary>
    private void OnDeleteSelectedLargeFiles(object sender, RoutedEventArgs e)
    {
        var rows = LargeFileList.SelectedItems.OfType<EntryRow>().ToList();
        if (rows.Count == 0)
        {
            _vm.StatusMessage = "삭제할 파일을 먼저 선택하세요 (Ctrl/Shift+클릭으로 여러 개 선택).";
            return;
        }
        _ = ShowDeleteReviewAsync(rows.Select(r => (r, r.Category)).ToList());
    }

    // ---------------------------------------------------------------- 폴더 탭 다중 선택 / 삭제

    private void OnFolderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView list) return;

        int folders = 0, files = 0;
        long bytes = 0;
        foreach (var item in list.SelectedItems)
        {
            if (item is not EntryRow row) continue;
            if (row.IsDirectory) folders++; else files++;
            bytes += row.Size;
        }

        int total = folders + files;
        FolderSelectionText.Text = total == 0
            ? "0개 항목 선택됨"
            : $"{total:N0}개 항목 선택됨 (폴더 {folders:N0} · 파일 {files:N0})";
        FolderSelectionSize.Text = SizeFormatter.Format(bytes);
    }

    private void OnClearFolderSelection(object sender, RoutedEventArgs e)
        => FolderList.SelectedItems.Clear();

    private void OnDeleteSelectedFolderItems(object sender, RoutedEventArgs e)
    {
        var rows = FolderList.SelectedItems.OfType<EntryRow>().ToList();
        if (rows.Count == 0)
        {
            _vm.StatusMessage = "삭제할 항목을 먼저 선택하세요 (Ctrl/Shift+클릭으로 여러 개 선택).";
            return;
        }
        _ = ShowDeleteReviewAsync(rows.Select(r => (r, r.Category)).ToList());
    }

    // ---------------------------------------------------------------- 공통 삭제 확인

    /// <summary>
    /// 삭제 확인 창을 띄우고, 실제로 사라진 항목을 데이터 모델에 즉시 반영한다(14).
    ///
    /// 판정(특히 폴더의 하위 탐색)은 I/O 라서 UI 스레드에서 돌리면 창이 뜨기 전에 멈춘 것처럼 보인다.
    /// 그래서 행 구성만 백그라운드로 돌리고, 창은 준비된 뒤에 연다.
    /// </summary>
    private async Task ShowDeleteReviewAsync(IReadOnlyList<(EntryRow Row, CategoryFlags Flags)> entries)
    {
        _vm.StatusMessage = $"{entries.Count:N0}개 항목의 삭제 가능 여부를 확인하는 중...";

        var reviewRows = await Task.Run(
            () => entries.Select(e => DeleteReviewRow.From(e.Row, e.Flags)).ToList())
            .ConfigureAwait(true);

        ShowDeleteReview(reviewRows);
    }

    private void ShowDeleteReview(IEnumerable<DeleteReviewRow> rows)
    {
        var window = new DeleteReviewWindow(rows) { Owner = this };
        window.ShowDialog();

        var summary = window.Summary;
        if (summary == null)
        {
            _vm.StatusMessage = "삭제를 취소했습니다.";
            return;
        }

        _vm.ApplyDeletion(summary.RemovedNodes);
        UpdateTreemap();
        OnLargeFileSelectionChanged(LargeFileList, null!);
        OnFolderSelectionChanged(FolderList, null!);
    }

    // ---------------------------------------------------------------- 53~66. 정리 추천

    private void OnCleanupRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView { SelectedItem: CleanupCandidate row })
            ShellService.OpenInExplorer(row.FullPath, row.IsDirectory);
    }

    /// <summary>정리 추천 탭의 삭제도 같은 확인 창을 거친다(7. 즉시 삭제 금지).</summary>
    private void OnCleanupDelete(object sender, RoutedEventArgs e)
    {
        var selected = _vm.Cleanup.SelectedItems;
        if (selected.Count == 0)
        {
            _vm.StatusMessage = "정리할 항목을 먼저 선택하세요.";
            return;
        }
        // 정리 추천 후보는 이미 분석 단계에서 보호 등급이 확정되어 있으므로 그대로 넘긴다.
        ShowDeleteReview(selected.Select(DeleteReviewRow.From));
    }

    /// <summary>
    /// 60. 안전 삭제 기본 정책.
    ///  - 기본은 휴지통. 영구 삭제는 버튼을 따로 누른 경우에만.
    ///  - "확인 필요 / 삭제 비추천" 항목이 섞여 있으면 개수를 명시해서 한 번 더 알린다.
    ///  - 영구 삭제는 확인창을 두 번 거친다.
    /// 프로그램이 스스로 안전하다고 판단해 삭제하는 경로는 존재하지 않는다.
    /// </summary>
    private async void RunCleanup(bool permanent)
    {
        var selected = _vm.Cleanup.SelectedItems;
        if (selected.Count == 0)
        {
            _vm.StatusMessage = "정리할 항목을 먼저 선택하세요.";
            return;
        }

        long total = selected.Sum(c => c.Size);
        int review = selected.Count(c => c.Grade != CleanupGrade.HighlyCleanable);
        int protectedCount = selected.Count(c => Categorizer.IsProtectedPath(c.FullPath));
        string action = permanent ? "영구 삭제" : "휴지통으로 이동";

        var message = new System.Text.StringBuilder();
        message.AppendLine($"{selected.Count:N0}개 항목을 {action} 합니다.");
        message.AppendLine($"예상 확보 용량: {SizeFormatter.Format(total)}");
        if (review > 0) message.AppendLine($"\n· 확인이 필요한 항목 {review:N0}개가 포함되어 있습니다.");
        if (protectedCount > 0) message.AppendLine($"· ⚠ 시스템 보호 경로 항목이 {protectedCount:N0}개 포함되어 있습니다.");
        if (permanent) message.AppendLine("\n영구 삭제한 파일은 복구할 수 없습니다.");

        var answer = MessageBox.Show(this, message.ToString(), $"{action} 확인",
            MessageBoxButton.OKCancel,
            permanent || protectedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        if (permanent)
        {
            var second = MessageBox.Show(this,
                $"정말 {selected.Count:N0}개 항목을 영구 삭제하시겠습니까?\n휴지통을 거치지 않으므로 되돌릴 수 없습니다.",
                "영구 삭제 - 한 번 더 확인", MessageBoxButton.YesNo, MessageBoxImage.Stop, MessageBoxResult.No);
            if (second != MessageBoxResult.Yes) return;
        }

        _vm.StatusMessage = $"{action} 진행 중... (0 / {selected.Count:N0})";

        // 파일 수천 개를 지우는 동안 UI 가 멈추면 안 된다(46).
        var (done, failed, freed) = await Task.Run(() =>
        {
            int ok = 0, fail = 0;
            long bytes = 0;
            foreach (var c in selected)
            {
                bool success = permanent
                    ? ShellService.DeletePermanently(c.FullPath)
                    : ShellService.MoveToRecycleBin(c.FullPath);
                if (success) { ok++; bytes += c.Size; }
                else fail++;
            }
            return (ok, fail, bytes);
        }).ConfigureAwait(true);

        _vm.Cleanup.RemoveDeleted(selected.Where(c => !PathStillExists(c)));
        _vm.StatusMessage = failed == 0
            ? $"{action} 완료 - {done:N0}개 / {SizeFormatter.Format(freed)} 확보 (목록 갱신은 새로고침 후 정확해집니다)"
            : $"{action} 완료 - 성공 {done:N0}개, 실패 {failed:N0}개 / {SizeFormatter.Format(freed)} 확보";
    }

    private static bool PathStillExists(CleanupCandidate c)
        => c.IsDirectory ? Directory.Exists(c.FullPath) : File.Exists(c.FullPath);

    // ---------------------------------------------------------------- 48. Export

    private void OnExportCsv(object sender, RoutedEventArgs e) => Export(csv: true);

    private void OnExportJson(object sender, RoutedEventArgs e) => Export(csv: false);

    private void Export(bool csv)
    {
        var result = _vm.CurrentResult;
        if (result == null) { _vm.StatusMessage = "내보낼 스캔 결과가 없습니다."; return; }

        var dialog = new SaveFileDialog
        {
            Filter = csv ? "CSV 파일|*.csv" : "JSON 파일|*.json",
            FileName = $"diskanalyzer_{DateTime.Now:yyyyMMdd_HHmmss}{(csv ? ".csv" : ".json")}",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            switch (Tabs.SelectedIndex)
            {
                case 1:
                    if (csv) ExportService.ExportCsv(dialog.FileName, _vm.TopFiles);
                    else ExportService.ExportJson(dialog.FileName, _vm.TopFiles);
                    break;
                case 2:
                    if (csv) ExportService.ExportExtensionsCsv(dialog.FileName, _vm.Extensions);
                    else ExportService.ExportJson(dialog.FileName, _vm.ExtensionFiles);
                    break;
                default:
                    if (csv) ExportService.ExportCsv(dialog.FileName, _vm.Rows);
                    else ExportService.ExportJson(dialog.FileName, _vm.Rows);
                    break;
            }
            _vm.StatusMessage = "내보내기 완료: " + dialog.FileName;
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = "내보내기 실패: " + ex.Message;
        }
    }
}
