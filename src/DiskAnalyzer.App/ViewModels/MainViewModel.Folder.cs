using System.Windows;
using DiskAnalyzer.Core.Keymaps;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Scanning;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>
/// 폴더 / Treemap 탭의 "지금 열려 있는 폴더" 관련 기능: 경로 막대(주소 입력 · 즐겨찾기)와 폴더 단위 새로고침.
///
/// 스캔 결과는 스캔한 순간의 스냅샷이라, 앱을 다시 켜서 이전 결과를 불러왔거나 오래 열어 두면 실제와 어긋난다.
/// 그래서 폴더를 열 때(그리고 창으로 돌아올 때) 그 폴더의 직속 항목만 가볍게 비교해서 달라진 것이 있으면 알린다.
/// 알리기만 하고 저장소는 건드리지 않는다 — 사용자가 새로고침(F5)을 눌렀을 때만 반영한다. 목록이 사용자 모르게 움직이지 않는다.
/// </summary>
public sealed partial class MainViewModel
{
    private PathBarViewModel? _pathBar;
    private CancellationTokenSource? _changeCts;
    private FolderDiff? _diff;
    private DateTime _lastChangeCheck = DateTime.MinValue;
    private bool _isFolderRefreshing;
    private RelayCommand? _refreshFolderCommand;
    private RelayCommand? _refreshFolderDeepCommand;

    public PathBarViewModel PathBar
    {
        get
        {
            if (_pathBar != null) return _pathBar;
            _pathBar = new PathBarViewModel(this);
            HookQuickMoveWatchSuspension();
            return _pathBar;
        }
    }

    /// <summary>스캔이 끝난 저장소. 스캔 중에는 Aggregator 가 쓰고 있어 읽을 수 없으므로 null.</summary>
    internal NodeStore? Store => IsScanning ? null : _current?.Store;

    /// <summary>지금 열려 있는 폴더의 전체 경로. 검색 결과를 보는 중에도 "열려 있는 폴더" 를 돌려준다. 없으면 null.</summary>
    public string? CurrentFolderPath
    {
        get
        {
            var store = Store;
            return store == null ? null : store.GetDirectoryPath(_currentDirId);
        }
    }

    /// <summary>파일 경로를 입력했을 때: 그 파일이 든 폴더로 이동한 뒤 화면이 이 파일을 선택하게 한다.</summary>
    public event Action<int>? SelectFileRequested;

    public void RequestSelectFile(int fileId) => SelectFileRequested?.Invoke(fileId);

    // ---------------------------------------------------------------- 변경 감지

    public bool HasFolderChanges => _diff is { Any: true };

    public string FolderChangeText
    {
        get
        {
            if (_diff == null) return string.Empty;
            string key = Keymap.Current.GetText(CommandIds.Refresh);
            string action = key.Length == 0 ? "새로고침" : $"{key} 새로고침";
            return _diff.DirectoryGone
                ? $"⚠ 이 폴더가 없어졌습니다 · {action}"
                : $"⚠ 변경 감지 · {_diff.Summary} · {action}";
        }
    }

    public string FolderRefreshToolTip
    {
        get
        {
            string lastScan = _current == null
                ? string.Empty
                : $"\n마지막 전체 스캔: {_current.CompletedAt:yyyy-MM-dd HH:mm} ({DescribeAge(DateTime.Now - _current.CompletedAt)})";
            string title = DiskAnalyzer.App.Keymaps.ShortcutHint.Format("이 폴더를 실제 상태로 새로고침 {0}", CommandIds.Refresh);
            string deep = DiskAnalyzer.App.Keymaps.ShortcutHint.Format("하위 폴더 안쪽까지 정확히: {0}", CommandIds.RefreshFolderDeep);
            return $"{title}\n드라이브 전체를 다시 스캔하지 않습니다.\n{deep}" + lastScan;
        }
    }

    private static string DescribeAge(TimeSpan age)
    {
        if (age.TotalMinutes < 1) return "방금";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}분 전";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}시간 전";
        return $"{(int)age.TotalDays}일 전";
    }

    public bool IsFolderRefreshing
    {
        get => _isFolderRefreshing;
        private set
        {
            if (!Set(ref _isFolderRefreshing, value)) return;
            RefreshFolderCommand.RaiseCanExecuteChanged();
            RefreshFolderDeepCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>F5: 지금 폴더를 새로고침한다(직속 항목의 변경 반영 + 새로 생긴 하위 폴더는 안쪽까지).</summary>
    public RelayCommand RefreshFolderCommand => _refreshFolderCommand ??=
        new RelayCommand(() => _ = RefreshFolderAsync(deep: false), () => Store != null && !IsFolderRefreshing);

    /// <summary>Shift+F5: 지금 폴더 아래 전체를 다시 읽는다(기존 하위 폴더의 안쪽 변경까지 정확히).</summary>
    public RelayCommand RefreshFolderDeepCommand => _refreshFolderDeepCommand ??=
        new RelayCommand(() => _ = RefreshFolderAsync(deep: true), () => Store != null && !IsFolderRefreshing);

    /// <summary>폴더를 열었을 때(이동 · 스캔 완료 · 뒤로 가기 등) — 이전 알림을 지우고 새로 확인한다.</summary>
    private void OnFolderOpened()
    {
        SetFolderChange(null);
        CheckFolderChanges(force: true);
        RestartFolderWatch();
        RefreshFolderCommand.RaiseCanExecuteChanged();
        RefreshFolderDeepCommand.RaiseCanExecuteChanged();
        Raise(nameof(FolderRefreshToolTip));
    }

    /// <summary>스캔을 시작하거나 다른 결과로 바뀔 때 — 예전 알림은 더 이상 맞지 않는다.</summary>
    private void ResetFolderChange()
    {
        _changeCts?.Cancel();
        StopFolderWatch();
        SetFolderChange(null);
        RefreshFolderCommand.RaiseCanExecuteChanged();
        RefreshFolderDeepCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 지금 폴더의 직속 항목을 실제와 비교한다(백그라운드). 저장소는 읽기만 하므로 결과가 오는 사이 다른 폴더로 옮겨 갔으면 버린다.
    /// 창으로 돌아올 때마다 부르므로 <paramref name="force"/> 가 아니면 2초 안의 반복 호출은 무시한다.
    /// </summary>
    public async void CheckFolderChanges(bool force = false)
    {
        try
        {
            var store = Store;
            if (store == null || IsFolderRefreshing) return;
            if (!force && (DateTime.UtcNow - _lastChangeCheck).TotalSeconds < 2) return;
            _lastChangeCheck = DateTime.UtcNow;

            int dirId = _currentDirId;
            var snapshot = store.SnapshotChildren(dirId);   // 저장소 읽기는 UI 스레드에서 끝낸다
            var options = Options.Clone();

            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _changeCts, cts)?.Cancel();

            var diff = await Task.Run(() => FolderRefresher.Detect(snapshot, options), cts.Token);

            if (cts.IsCancellationRequested || dirId != _currentDirId || !ReferenceEquals(store, Store)) return;
            SetFolderChange(diff.Any ? diff : null);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 감지는 참고 정보다. 실패해도 화면은 그대로 둔다 */ }
    }

    private void SetFolderChange(FolderDiff? diff)
    {
        if (_diff == null && diff == null) return;
        _diff = diff;
        Raise(nameof(HasFolderChanges));
        Raise(nameof(FolderChangeText));
    }

    // ---------------------------------------------------------------- 열려 있는 동안의 감시
    // 폴더를 열어 둔 채 다른 프로그램이 파일을 바꾸면 곧바로 "변경 감지" 로 알린다(창으로 돌아올 때 확인하는 것의 보완).
    // 감시는 알림용일 뿐 저장소는 건드리지 않는다. 삭제 / 이동이 진행되는 동안은 폴더 핸들을 잡지 않도록 잠시 끈다.

    private System.IO.FileSystemWatcher? _folderWatcher;
    private System.Threading.Timer? _folderWatchTimer;
    private int _watchSuspended;
    private bool _quickMoveWatchHooked;
    private bool _quickMoveSuspended;
    private const int FolderWatchDebounceMs = 700;

    private void RestartFolderWatch()
    {
        StopFolderWatch();
        if (_watchSuspended > 0) return;

        var store = Store;
        if (store == null) return;

        try
        {
            var w = new System.IO.FileSystemWatcher(store.GetDirectoryPath(_currentDirId))
            {
                NotifyFilter = System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.DirectoryName | System.IO.NotifyFilters.Size,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += OnFolderWatchEvent;
            w.Deleted += OnFolderWatchEvent;
            w.Changed += OnFolderWatchEvent;
            w.Renamed += OnFolderWatchEvent;
            w.Error += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => CheckFolderChanges(force: true));
            w.EnableRaisingEvents = true;
            _folderWatcher = w;
        }
        catch (Exception)
        {
            _folderWatcher = null;   // 폴더가 이미 없거나 감시할 수 없는 위치. 열 때 · 창으로 돌아올 때 확인은 그대로 동작한다.
        }
    }

    private void StopFolderWatch()
    {
        var w = Interlocked.Exchange(ref _folderWatcher, null);
        if (w == null) return;
        w.EnableRaisingEvents = false;   // 핸들을 바로 놓아야 이 폴더를 지우거나 옮길 수 있다
        w.Dispose();
    }

    private void OnFolderWatchEvent(object sender, System.IO.FileSystemEventArgs e)
    {
        try
        {
            (_folderWatchTimer ??= new System.Threading.Timer(_ =>
                Application.Current?.Dispatcher.BeginInvoke(() => CheckFolderChanges(force: true)))).Change(FolderWatchDebounceMs, System.Threading.Timeout.Infinite);
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>삭제 확인 창 / 이동 실행처럼 폴더를 지우거나 옮기는 동안 감시를 끈다. <see cref="ResumeFolderWatch"/> 와 짝으로 부른다.</summary>
    public void SuspendFolderWatch()
    {
        _watchSuspended++;
        StopFolderWatch();
    }

    public void ResumeFolderWatch()
    {
        if (_watchSuspended > 0 && --_watchSuspended == 0) RestartFolderWatch();
    }

    /// <summary>빠른 이동을 실행하는 동안에도 감시를 끈다(옮기는 폴더의 핸들을 잡고 있지 않도록).</summary>
    private void HookQuickMoveWatchSuspension()
    {
        if (_quickMoveWatchHooked) return;
        _quickMoveWatchHooked = true;

        QuickMove.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(QuickMove.Phase)) return;
            bool running = QuickMove.Phase == global::DiskAnalyzer.App.ViewModels.QuickMove.QuickMovePhase.Running;
            if (running == _quickMoveSuspended) return;
            _quickMoveSuspended = running;
            if (running) SuspendFolderWatch(); else ResumeFolderWatch();
        };
    }

    // ---------------------------------------------------------------- 폴더 새로고침

    /// <summary>
    /// 지금 폴더를 실제 상태로 맞춘다. 디스크 읽기는 백그라운드에서, 저장소 반영은 UI 스레드에서 한 번에 한다.
    /// <paramref name="deep"/> 가 true 면 이 폴더 아래 전체를 다시 읽는다.
    /// </summary>
    public async Task RefreshFolderAsync(bool deep)
    {
        if (IsScanning) { StatusMessage = "스캔이 끝난 뒤에 새로고침할 수 있습니다."; return; }
        var store = Store;
        if (store == null || IsFolderRefreshing) return;

        IsFolderRefreshing = true;
        _changeCts?.Cancel();

        int dirId = _currentDirId;
        var snapshot = store.SnapshotChildren(dirId);
        var options = Options.Clone();
        StatusMessage = deep ? $"{snapshot.Path} 아래를 다시 스캔하는 중..." : $"{snapshot.Path} 새로고침 중...";

        FolderRefreshPlan? plan = null;
        string? error = null;
        try
        {
            plan = await Task.Run(() => FolderRefresher.Build(snapshot, options, deep));
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        try
        {
            if (plan == null) { StatusMessage = "새로고침 실패: " + error; return; }
            if (!ReferenceEquals(store, Store)) return;   // 그 사이 다른 스캔이 시작되었다

            // 정리 추천 분석이 저장소를 읽는 중이면 끝날 때까지 기다린다(읽는 도중에 배열이 바뀌지 않게).
            for (int i = 0; i < 300 && Cleanup.IsBusy; i++) await Task.Delay(100).ConfigureAwait(true);
            if (!ReferenceEquals(store, Store)) return;

            var result = store.ApplyRefresh(plan);

            // 폴더가 사라진 경우 그 상위도 함께 사라졌을 수 있다(상위 폴더를 통째로 지운 경우). 남아 있는 곳까지 올라간다.
            if (plan.DirectoryGone) await RemoveVanishedAncestorsAsync(store, dirId);
            if (!ReferenceEquals(store, Store)) return;

            if (store.IsDirectoryDeleted(_currentDirId))
                Navigate(store.NearestLiveDirectory(_currentDirId), pushHistory: false);
            else
                RefreshCurrentView();

            RefreshTabData();
            RaiseStatusText();
            if (result.Any) Cleanup.SetResult(_current);
            SetFolderChange(null);
            ViewRefreshed?.Invoke(this, EventArgs.Empty);

            StatusMessage = DescribeRefresh(snapshot.Path, plan, result, deep);
        }
        finally
        {
            IsFolderRefreshing = false;
        }
    }

    /// <summary>사라진 폴더의 상위를 차례로 디스크에서 확인해, 이미 없는 것은 저장소에서도 뺀다. 처음으로 실제 있는 폴더에서 멈춘다.</summary>
    private static async Task RemoveVanishedAncestorsAsync(NodeStore store, int goneDirId)
    {
        int cur = store.NearestLiveDirectory(store.GetParent(goneDirId));
        while (cur > NodeStore.RootId)
        {
            string path = store.GetDirectoryPath(cur);
            if (await Task.Run(() => System.IO.Directory.Exists(path))) break;

            int parent = store.GetParent(cur);
            store.RemoveNodes(new[] { (RowKind.Directory, cur) });
            cur = store.NearestLiveDirectory(parent);
        }
    }

    private static string DescribeRefresh(string path, FolderRefreshPlan plan, RefreshResult result, bool deep)
    {
        if (plan.DirectoryGone) return $"{path} 폴더가 없어져 목록에서 뺐습니다.";
        if (!plan.Diff.Readable) return $"{path} 을(를) 읽을 수 없어 그대로 두었습니다(접근 권한 확인).";

        string scope = deep ? "하위까지 다시 스캔" : "새로고침";
        if (!result.Any) return $"{path} {scope} — 변경 없음";

        var parts = new List<string>();
        if (result.AddedFiles > 0) parts.Add($"파일 +{result.AddedFiles:N0}");
        if (result.RemovedFiles > 0) parts.Add($"파일 −{result.RemovedFiles:N0}");
        if (result.ResizedFiles > 0) parts.Add($"크기 변경 {result.ResizedFiles:N0}");
        if (result.AddedDirectories > 0) parts.Add($"폴더 +{result.AddedDirectories:N0}");
        if (result.RemovedDirectories > 0) parts.Add($"폴더 −{result.RemovedDirectories:N0}");

        string net = result.NetBytes == 0 ? string.Empty : $" · 전체 {(result.NetBytes > 0 ? "+" : "−")}{SizeFormatter.Format(Math.Abs(result.NetBytes))}";
        return $"{path} {scope}: {string.Join(", ", parts)}{net}";
    }
}
