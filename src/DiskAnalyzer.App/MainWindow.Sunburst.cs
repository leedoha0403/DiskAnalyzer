using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskAnalyzer.App.ViewModels;
using DiskAnalyzer.App.Controls;
using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Scanning;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App;

/// <summary>
/// 선버스트 탭과 수집함.
///
/// <para>선버스트는 폴더 · Treemap 과 <b>같은 위치 상태</b>를 쓴다 — 뒤로 / 앞으로 / 상위 / 경로 막대가
/// 그대로 듣고, 링에서 들어간 폴더는 폴더 목록에서도 열려 있다. 탭은 "같은 곳을 다르게 보는 창"이지
/// 별개의 화면이 아니다.</para>
/// </summary>
public partial class MainWindow
{
    private const string SunburstHint =
        "조각을 가리키면 오른쪽 목록이 그 폴더의 내용으로 바뀝니다(이동하지 않습니다). " +
        "누르면 들어가고, 가운데 원을 누르면 상위로 올라갑니다. 가운데 버튼 / 우클릭으로 수집함에 담습니다.";

    private void InitializeSunburst()
    {
        Sunburst.HoverChanged += OnSunburstHover;
        Sunburst.ItemActivated += OnSunburstActivated;
        Sunburst.ItemSelected += OnSunburstSelected;
        Sunburst.GoUpRequested += (_, _) => _vm.UpCommand.Execute(null);
        Sunburst.CollectRequested += (_, s) => CollectSegment(s);
        Sunburst.DragStartRequested += OnSunburstDragStart;

        _vm.Collector.Changed += (_, _) =>
        {
            _vm.RaiseCollectorText();
            RefreshCollectorView();

            // 담긴 양은 화면 맨 아래 한 줄로만 바뀐다. 눈이 거기 가 있지 않으면 바뀐 줄 모른다.
            Motion.Bump(CollectorCount);
        };

        // 설정에서 애니메이션을 끄면 링이 반쯤 줌인된 채로 멈춰 있으면 안 된다.
        Motion.EnabledChanged += (_, _) =>
        {
            if (Motion.Enabled) return;
            Sunburst.StopMotion();
            Treemap.StopMotion();
        };

        SunburstInfo.Text = SunburstHint;
        RefreshCollectorView();
        _vm.RefreshPinnedTargets();

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.RingTint)) _ = ApplyRingTintAsync();
        };
    }

    /// <summary>고정한 폴더 카드: 한 번 누르면 경로로 이동, 두 번이면 스캔(드라이브 카드와 같은 규칙).</summary>
    private void OnPinnedCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        if (e.ClickCount >= 2) _vm.StartScan(path);
        else _vm.NavigateToPath(path);
    }

    private void OnPinnedScan(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) _vm.StartScan(path);
    }

    /// <summary>
    /// 링을 현재 폴더로 다시 그린다.
    /// 스캔 중에는 Aggregator 스레드가 <see cref="NodeStore"/> 를 쓰고 있으므로 타고 내려가지 않는다.
    /// </summary>
    private void UpdateSunburst()
    {
        if (SunburstPanel.Visibility != Visibility.Visible) return;

        if (_vm.IsScanning || _vm.CurrentResult == null)
        {
            // 스캔 중에는 한 겹만. 링이 채워지는 것이 그대로 보이므로 진행 표시가 따로 필요 없다.
            Sunburst.SetFlatSource(_vm.Rows, _vm.CurrentPath ?? string.Empty);
            _vm.ShowSunburstSidebarFromRows(_vm.Rows, _vm.CurrentPath ?? string.Empty);
            SunburstInfo.Text = _vm.IsScanning
                ? "스캔 중에는 현재 폴더의 한 겹만 그립니다. 끝나면 하위 겹까지 함께 보입니다."
                : SunburstHint;
            return;
        }

        Sunburst.SetSource(_vm.CurrentResult.Store, _vm.CurrentDirectoryId);
        _vm.ShowSunburstSidebar(_vm.CurrentDirectoryId);
        SyncCollectedToSunburst();
        SunburstInfo.Text = SunburstHint;
        _ = ApplyRingTintAsync();
    }

    // ---------------------------------------------------------------- 링 색 기준

    // 직전 스캔은 "이전 대비 증감"을 처음 켤 때만 읽는다 - 압축 해제라 비싸고, 대부분의 사용에서는 필요 없다.
    private ScanResult? _previousScan;
    private ScanResult? _previousScanFor;

    // 지금 화면에 칠해진 겹침 정보. 색만 보여 주면 "얼마나" 를 알 수 없어 설명 줄에 숫자로도 적는다.
    private Dictionary<long, int>? _tintScores;
    private Dictionary<long, long>? _tintPrevious;

    /// <summary>
    /// 링의 색을 지금 고른 기준으로 칠한다. 각도는 그대로다 —
    /// <b>같은 그림을 다른 질문으로 읽는 것</b>이지 다른 그림을 그리는 것이 아니다.
    /// </summary>
    private async Task ApplyRingTintAsync()
    {
        if (SunburstPanel.Visibility != Visibility.Visible) return;

        switch (_vm.RingTint)
        {
            case SunburstTint.Cleanup:
                var scores = CleanupScores();
                _tintScores = scores;
                _tintPrevious = null;
                Sunburst.SetTint(SunburstTint.Cleanup, scores);
                SunburstInfo.Text = scores.Count > 0
                    ? $"색 = 정리 추천 점수. 후보 {scores.Count:N0}건이 초록(참고) → 빨강(우선 정리) 으로 칠해집니다. 후보가 아닌 것은 회색입니다."
                    : "정리 추천 결과가 없습니다. [정리 추천] 탭에서 먼저 분석하세요.";
                return;

            case SunburstTint.Delta:
                var previous = await EnsurePreviousScanAsync().ConfigureAwait(true);
                if (previous == null)
                {
                    _tintScores = null;
                    _tintPrevious = null;
                    Sunburst.SetTint(SunburstTint.Size);
                    SunburstInfo.Text = "비교할 직전 스캔이 없습니다. 같은 대상을 한 번 더 스캔하면 그때부터 증감을 볼 수 있습니다.";
                    return;
                }

                var sizes = ScanDiff.PreviousSizes(
                    _vm.CurrentResult?.Store, previous.Store, Sunburst.Layout, _vm.CurrentDirectoryId);
                _tintScores = null;
                _tintPrevious = sizes;
                Sunburst.SetTint(SunburstTint.Delta, previous: sizes);
                SunburstInfo.Text =
                    $"색 = {previous.CompletedAt:yyyy-MM-dd HH:mm} 스캔 대비 증감. 붉을수록 늘고 푸를수록 줄었으며, 보라는 그때 없던 항목입니다.";
                return;

            default:
                _tintScores = null;
                _tintPrevious = null;
                Sunburst.SetTint(SunburstTint.Size);
                SunburstInfo.Text = SunburstHint;
                return;
        }
    }

    /// <summary>정리 추천 후보의 점수. 후보가 아닌 것은 넣지 않는다(회색으로 눕는다).</summary>
    private Dictionary<long, int> CleanupScores()
    {
        var map = new Dictionary<long, int>(_vm.Cleanup.Candidates.Count);
        foreach (var c in _vm.Cleanup.Candidates)
        {
            var kind = c.Kind == RowKind.Directory ? SunburstKind.Directory : SunburstKind.File;
            map[SunburstLayout.KeyOf(kind, c.Id)] = c.Score;
        }
        return map;
    }

    private async Task<ScanResult?> EnsurePreviousScanAsync()
    {
        var current = _vm.CurrentResult;
        if (current == null) return null;
        if (ReferenceEquals(_previousScanFor, current)) return _previousScan;

        _previousScanFor = current;
        string root = current.RootPath;
        _previousScan = await Task.Run(() => CacheService.TryLoadPrevious(root)).ConfigureAwait(true);
        return _previousScan;
    }

    /// <summary>
    /// 가리키면 <b>이동하지 않고</b> 그 폴더의 내용을 옆 목록에 보여 준다.
    /// DaisyDisk 동선의 핵심이다 — 훑어보는 데 클릭 비용이 들지 않는다.
    /// </summary>
    private void OnSunburstHover(object? sender, SunburstSegment? segment)
    {
        if (segment == null)
        {
            _vm.ShowSunburstSidebar(_vm.CurrentDirectoryId);
            SunburstInfo.Text = SunburstHint;
            return;
        }

        SunburstInfo.Text = DescribeSegment(segment);

        if (segment.Kind == SunburstKind.Directory) _vm.ShowSunburstSidebar(segment.Id, peeking: true);
        else _vm.ShowSunburstSidebar(_vm.CurrentDirectoryId);
    }

    private string DescribeSegment(SunburstSegment segment)
    {
        string share = $"{segment.Sweep / 360d * 100d:0.##}%";

        if (segment.Kind == SunburstKind.Smaller)
            return $"{segment.Name}   |   {SizeFormatter.Format(segment.Size)}   |   {share}   |   " +
                   "따로 그리기엔 너무 얇아 합쳐진 묶음입니다 (개별 삭제는 폴더 탭에서)";

        string kind = segment.Kind == SunburstKind.Directory ? "폴더" : "파일";
        string path = Sunburst.PathOf(segment);
        string collected = _vm.Collector.Covers(path) ? "   [수집함]" : string.Empty;
        return $"{segment.Name}   |   {SizeFormatter.Format(segment.Size)}   |   {share}{DescribeTint(segment)}   |   {kind}   |   {path}{collected}";
    }

    /// <summary>
    /// 지금 칠해진 색이 무엇을 뜻하는지 숫자로도 적는다.
    /// 색만으로는 "늘었다"는 알아도 "얼마나"는 알 수 없다.
    /// </summary>
    private string DescribeTint(SunburstSegment segment)
    {
        if (segment.Id < 0) return string.Empty;
        long key = SunburstLayout.KeyOf(segment.Kind, segment.Id);

        if (_tintScores != null)
        {
            return _tintScores.TryGetValue(key, out int score)
                ? $"   |   정리 추천 {score}점"
                : "   |   정리 후보 아님";
        }

        if (_tintPrevious == null) return string.Empty;

        if (!_tintPrevious.TryGetValue(key, out long before)) return "   |   이전 스캔에 없던 항목";

        long delta = segment.Size - before;
        if (delta == 0) return "   |   이전과 같음";

        string sign = delta > 0 ? "+" : "−";
        return $"   |   이전 {SizeFormatter.Format(before)} → {sign}{SizeFormatter.Format(Math.Abs(delta))}";
    }

    /// <summary>폴더 조각은 한 번 누르면 들어간다. 폴더 탭과 같은 위치 상태를 쓴다.</summary>
    private void OnSunburstActivated(object? sender, SunburstSegment segment)
    {
        if (segment.Kind != SunburstKind.Directory) return;
        _vm.Navigate(segment.Id);
    }

    private void OnSunburstSelected(object? sender, SunburstSegment segment)
    {
        SunburstInfo.Text = DescribeSegment(segment);
        if (segment.Id >= 0)
            Sunburst.SetSelection(new[] { (segment.Kind == SunburstKind.Directory, segment.Id) });
    }

    /// <summary>옆 목록에서 폴더 줄을 더블 클릭하면 링과 함께 그 폴더로 들어간다.</summary>
    private void OnSunburstSidebarActivate(object sender, MouseButtonEventArgs e)
    {
        if (SunburstList.SelectedItem is SunburstSidebarRow { CanNavigate: true } row) _vm.Navigate(row.Id);
    }

    // ---------------------------------------------------------------- 우클릭 메뉴

    private void OnSunburstMenuOpened(object sender, RoutedEventArgs e)
    {
        var item = Sunburst.ContextItem;
        bool onItem = item is { Id: >= 0 };

        SbCollect.Visibility = item is { CanCollect: true } ? Visibility.Visible : Visibility.Collapsed;
        SbOpenExplorer.Visibility = onItem ? Visibility.Visible : Visibility.Collapsed;
        SbCopyPath.Visibility = onItem ? Visibility.Visible : Visibility.Collapsed;

        if (item is { CanCollect: true })
            SbCollect.Header = _vm.Collector.Contains(Sunburst.PathOf(item)) ? "수집함에서 빼기" : "수집함에 담기";

        string? favorite = SunburstFavoriteTarget();
        SbFavorite.Visibility = favorite != null ? Visibility.Visible : Visibility.Collapsed;
        if (favorite != null)
            SbFavorite.Header = _vm.PathBar.IsFavoritePath(favorite) ? "즐겨찾기에서 제거" : "즐겨찾기에 추가";
    }

    private string? SunburstFavoriteTarget()
        => Sunburst.ContextItem is { } item
            ? (item.Kind == SunburstKind.Directory ? Sunburst.PathOf(item) : null)
            : _vm.CurrentFolderPath;

    private void OnSunburstFavorite(object sender, RoutedEventArgs e)
    {
        if (SunburstFavoriteTarget() is { } target) _vm.PathBar.ToggleFavorite(target);
    }

    private void OnSunburstOpenExplorer(object sender, RoutedEventArgs e)
    {
        if (Sunburst.ContextItem is not { Id: >= 0 } item) return;
        ShellService.OpenInExplorer(Sunburst.PathOf(item), item.Kind == SunburstKind.Directory);
    }

    private void OnSunburstCopyPath(object sender, RoutedEventArgs e)
    {
        if (Sunburst.ContextItem is { Id: >= 0 } item) TrySetClipboard(Sunburst.PathOf(item));
    }

    private void OnSunburstCollect(object sender, RoutedEventArgs e)
    {
        if (Sunburst.ContextItem is { } item) CollectSegment(item);
    }

    // ---------------------------------------------------------------- 수집함

    /// <summary>
    /// 조각 하나를 담거나 뺀다. 보호 등급은 담는 순간 판정한다 —
    /// P0 는 수집함에 들어가지 못하므로 삭제 확인 창까지 갈 일이 없다.
    /// </summary>
    private void CollectSegment(SunburstSegment segment)
    {
        if (!segment.CanCollect)
        {
            _vm.StatusMessage = "합쳐진 묶음은 통째로 담을 수 없습니다. 폴더 탭에서 개별로 고르세요.";
            return;
        }

        string path = Sunburst.PathOf(segment);
        if (path.Length == 0) return;

        bool isDir = segment.Kind == SunburstKind.Directory;
        if (_vm.Collector.Remove(path))
        {
            _vm.StatusMessage = $"수집함에서 뺐습니다 — {segment.Name}";
        }
        else
        {
            var flags = CategoryOf(segment, isDir);
            var result = _vm.Collector.TryAdd(path, segment.Name, isDir, segment.Size, flags, segment.Id);
            _vm.StatusMessage = ExplainCollect(result, segment.Name);
        }

        SyncCollectedToSunburst();
    }

    private CategoryFlags CategoryOf(SunburstSegment segment, bool isDirectory)
    {
        var store = _vm.CurrentResult?.Store;
        if (store == null || segment.Id < 0) return CategoryFlags.None;

        return isDirectory
            ? store.GetDirectoryCategory(segment.Id)
            : store.GetFileCategory(segment.Id);
    }

    private static string ExplainCollect(CollectResult result, string name) => result switch
    {
        CollectResult.Added => $"수집함에 담았습니다 — {name}",
        CollectResult.Duplicate => $"이미 담겨 있습니다 — {name}",
        CollectResult.CoveredByFolder => $"이미 담긴 폴더 안에 있습니다 — {name}",
        CollectResult.Protected => $"보호된 항목이라 담을 수 없습니다 (P0) — {name}",
        _ => $"담을 수 없는 항목입니다 — {name}",
    };

    private void SyncCollectedToSunburst()
    {
        if (SunburstPanel.Visibility != Visibility.Visible) return;
        Sunburst.SetCollected(_vm.Collector.Items.Select(i => (i.IsDirectory, i.Id)));
    }

    private void RefreshCollectorView()
    {
        CollectorList.ItemsSource = _vm.Collector.Items;
        if (_vm.Collector.IsEmpty)
        {
            CollectorExpand.IsChecked = false;
            Motion.SetOpen(CollectorPanel, false);
        }
        SyncCollectedToSunburst();
    }

    private void OnCollectorExpandChanged(object sender, RoutedEventArgs e)
        => Motion.SetOpen(CollectorPanel, CollectorExpand.IsChecked == true && !_vm.Collector.IsEmpty);

    private void OnCollectorRemove(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) _vm.Collector.Remove(path);
    }

    private void OnCollectorToQueue(object sender, RoutedEventArgs e) => _ = SendCollectorToQueueAsync();

    /// <summary>
    /// 수집함을 <b>지우는 대신 옮긴다</b>. 이 앱의 두 축(모아 두기 · 옮기기)을 잇는 지점이다 —
    /// 여기저기 돌아다니며 담은 것을 그대로 다른 드라이브로 넘길 수 있다.
    ///
    /// <para>보낸 뒤에는 수집함을 비운다. 남겨 두면 같은 항목이 "옮길 것"이면서 동시에
    /// "지울 것"으로 남아, 실수로 [삭제...] 를 누를 여지가 생긴다.</para>
    /// </summary>
    private async Task SendCollectorToQueueAsync()
    {
        var paths = _vm.Collector.Items.Select(i => i.FullPath).ToList();
        if (paths.Count == 0)
        {
            _vm.StatusMessage = "수집함이 비어 있습니다.";
            return;
        }

        RevealQuickMove();
        await _vm.QuickMove.AddPathsToQueueAsync(paths).ConfigureAwait(true);
        _vm.Collector.Clear();

        _vm.StatusMessage =
            $"{paths.Count:N0}개를 이동 대기열로 보냈습니다. 목적지를 고른 뒤 [이동 시작] 을 누르면 옮겨집니다.";
    }

    private void OnCollectorClear(object sender, RoutedEventArgs e)
    {
        _vm.Collector.Clear();
        _vm.StatusMessage = "수집함을 비웠습니다.";
    }

    /// <summary>
    /// 수집함의 삭제도 <b>기존 확인 창을 그대로 거친다</b>. 수집함은 "고르는 곳"이지 "지우는 곳"이 아니다.
    /// 성공한 것만 수집함에서 빠지고 실패한 것은 남아 다시 시도할 수 있다.
    /// </summary>
    private void OnCollectorDelete(object sender, RoutedEventArgs e) => _ = DeleteCollectedAsync();

    private async Task DeleteCollectedAsync()
    {
        var items = _vm.Collector.Items;
        if (items.Count == 0)
        {
            _vm.StatusMessage = "수집함이 비어 있습니다.";
            return;
        }

        // 보호 등급 재판정과 수정일 조회는 I/O 다. 창이 뜨기 전에 멈춘 것처럼 보이지 않게 백그라운드에서 만든다.
        _vm.StatusMessage = $"{items.Count:N0}개 항목의 삭제 가능 여부를 확인하는 중...";
        var rows = await Task.Run(() => items.Select(DeleteReviewRow.From).ToList()).ConfigureAwait(true);

        var before = items.Select(i => i.FullPath).ToList();
        ShowDeleteReview(rows);

        // 실제로 사라진 것만 걷어낸다. 실패한 것은 남겨 다시 시도할 수 있게 둔다.
        _vm.Collector.RemoveMany(before.Where(p => !File.Exists(p) && !Directory.Exists(p)));
        RefreshCollectorView();
        UpdateSunburst();
    }

    // ---------------------------------------------------------------- 끌어서 담기 / 떨어뜨려 스캔

    /// <summary>수집함으로 끌어다 놓기. 컨트롤은 조각만 알려 주고 데이터 형식은 여기서 정한다.</summary>
    private void OnSunburstDragStart(object? sender, SunburstSegment segment)
    {
        string path = Sunburst.PathOf(segment);
        if (path.Length == 0) return;

        _vm.StatusMessage = "수집함(아래 막대)에 떨어뜨리면 담깁니다.";
        DragDrop.DoDragDrop(Sunburst, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
    }

    private static string[] DroppedPaths(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] p
            ? p
            : [];

    private void OnCollectorDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPaths(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// 떨어뜨린 경로를 수집함에 담는다. 스캔 결과 안에 있는 것만 받는다 —
    /// 크기와 보호 등급을 모르는 채로 삭제 대기열에 넣지 않기 위함이다.
    /// </summary>
    private void OnCollectorDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var paths = DroppedPaths(e);
        if (paths.Length == 0) return;

        var store = _vm.CurrentResult?.Store;
        if (store == null)
        {
            _vm.StatusMessage = "스캔 결과가 없어 담을 수 없습니다.";
            return;
        }

        int added = 0, refused = 0;
        foreach (var (kind, id) in store.FindNodes(paths))
        {
            bool isDir = kind == RowKind.Directory;
            string full = isDir ? store.GetDirectoryPath(id) : store.GetFilePath(id);
            string name = isDir ? store.GetDirectoryName(id) : store.GetFileName(id);
            long size = isDir ? store.GetDirectorySize(id) : store.GetFileSize(id);

            if (_vm.Collector.TryAdd(full, name, isDir, size, CategoryFlags.None, id) == CollectResult.Added) added++;
            else refused++;
        }

        _vm.StatusMessage = added > 0
            ? $"수집함에 {added:N0}개를 담았습니다." + (refused > 0 ? $" ({refused:N0}개는 담기지 않았습니다)" : string.Empty)
            : "담을 수 있는 항목이 없었습니다(보호 대상이거나 이미 담겨 있습니다).";
        SyncCollectedToSunburst();
    }

    /// <summary>탐색기에서 폴더 · 드라이브를 창에 떨어뜨리면 그 대상을 스캔한다.</summary>
    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPaths(e).Any(Directory.Exists) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        string? folder = DroppedPaths(e).FirstOrDefault(Directory.Exists);
        if (folder == null) return;

        if (_vm.IsScanning)
        {
            _vm.StatusMessage = "스캔이 진행 중입니다. 끝난 뒤에 다시 떨어뜨리세요.";
            return;
        }
        _vm.StartScan(folder);
    }

    // ---------------------------------------------------------------- 목록에서 담기

    /// <summary>
    /// 단축키(기본 Ctrl+Delete)로 담는다. 무엇을 담을지는 지금 보이는 화면이 정한다 —
    /// 선버스트는 <b>가리킨 조각</b>, 목록은 <b>고른 줄들</b>이다.
    /// </summary>
    private bool CollectFocusedSelection()
    {
        // 마우스를 올려 둔 것이 있으면 그것, 없으면 키보드가 가리키는 것.
        if (SunburstPanel.Visibility == Visibility.Visible && (Sunburst.Hovered ?? Sunburst.Focused) is { } pointed)
        {
            CollectSegment(pointed);
            return true;
        }

        if (FolderPanel.Visibility == Visibility.Visible && RealSelection(FolderList.SelectedItems) is { Count: > 0 } rows)
            return CollectRows(rows);

        if (LargeFilesPanel.Visibility == Visibility.Visible)
        {
            var files = LargeFileList.SelectedItems.OfType<EntryRow>().ToList();
            if (files.Count > 0) return CollectRows(files);
        }

        return false;
    }

    private void OnRowCollect(object sender, RoutedEventArgs e)
    {
        var rows = RealSelection(FolderList.SelectedItems);
        if (rows.Count == 0 && RowOf(sender) is { } one) rows = new List<EntryRow> { one };
        if (rows.Count > 0) CollectRows(rows);
    }

    private bool CollectRows(IReadOnlyList<EntryRow> rows)
    {
        int added = 0, protectedCount = 0, already = 0;

        foreach (var row in rows)
        {
            switch (_vm.Collector.TryAdd(row.FullPath, row.Name, row.IsDirectory, row.Size, row.Category, row.Id))
            {
                case CollectResult.Added: added++; break;
                case CollectResult.Protected: protectedCount++; break;
                default: already++; break;
            }
        }

        var parts = new List<string>(3);
        if (added > 0) parts.Add($"{added:N0}개 담음");
        if (already > 0) parts.Add($"{already:N0}개는 이미 담겨 있음");
        if (protectedCount > 0) parts.Add($"{protectedCount:N0}개는 보호 대상(P0)이라 담기지 않음");

        _vm.StatusMessage = parts.Count > 0 ? "수집함 - " + string.Join(" · ", parts) : "담을 항목이 없습니다.";
        SyncCollectedToSunburst();
        return added > 0;
    }
}
