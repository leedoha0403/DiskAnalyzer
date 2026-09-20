using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskAnalyzer.App.Keymaps;
using DiskAnalyzer.App.Views;
using DiskAnalyzer.Core.Keymaps;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.QuickMove;
using DiskAnalyzer.Core.Scanning;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App;

/// <summary>
/// 창 수준 단축키. 어떤 키가 어떤 명령인지는 <see cref="Keymap.Current"/> 가 정하고(설정 화면에서 바꾼다),
/// 여기서는 명령 ID 별로 "지금 화면에서 무엇을 할지" 만 안다. 키 이름은 이 파일 어디에도 없다.
/// 빠른 이동 탭 안의 명령은 QuickMoveView / QuickMovePane 이 IShortcutTarget 으로 먼저 처리한다.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] TabCommands =
    {
        CommandIds.TabsFolder, CommandIds.TabsTreemap, CommandIds.TabsLargeFiles,
        CommandIds.TabsFileTypes, CommandIds.TabsCleanup, CommandIds.TabsQuickMove,
    };

    private void InstallShortcuts()
    {
        new ShortcutRouter(Keymap.Current, ExecuteShortcut).Attach(this);
    }

    private void OnOpenShortcutSettings(object sender, RoutedEventArgs e)
    {
        var window = new KeyboardShortcutsWindow(Keymap.Current) { Owner = this };
        window.ShowDialog();
    }

    private bool ExecuteShortcut(string commandId)
    {
        int tabIndex = Array.IndexOf(TabCommands, commandId);
        if (tabIndex >= 0)
        {
            Tabs.SelectedIndex = tabIndex;
            return true;
        }

        bool onNavTab = NavBar.Visibility == Visibility.Visible;

        switch (commandId)
        {
            case CommandIds.TabsNext:
                Tabs.SelectedIndex = (Tabs.SelectedIndex + 1) % Tabs.Items.Count;
                return true;
            case CommandIds.TabsPrevious:
                Tabs.SelectedIndex = (Tabs.SelectedIndex - 1 + Tabs.Items.Count) % Tabs.Items.Count;
                return true;

            case CommandIds.GoParent:
            case CommandIds.GoParentAlt:
                return onNavTab && Run(_vm.UpCommand);
            case CommandIds.GoBack:
                return onNavTab && Run(_vm.BackCommand);
            case CommandIds.GoForward:
                return onNavTab && Run(_vm.ForwardCommand);
            case CommandIds.GoRoot:
                return onNavTab && Run(_vm.RootCommand);
            case CommandIds.FocusPath:
                if (!onNavTab) return false;
                _vm.PathBar.BeginEdit();
                return _vm.PathBar.IsEditing;
            case CommandIds.ToggleFavorite:
                return onNavTab && Run(_vm.PathBar.ToggleFavoriteCommand);
            case CommandIds.RefreshFolderDeep:
                return onNavTab && Run(_vm.RefreshFolderDeepCommand);

            case CommandIds.SearchFocus:
                if (FilterBar.Visibility != Visibility.Visible) return false;
                SearchBox.Focus();
                SearchBox.SelectAll();
                return true;
            case CommandIds.SearchCancel:
                return CancelSearchOrSelection();

            case CommandIds.Refresh:
                return RefreshCurrentScreen();
            case CommandIds.ForceRescan:
                return Run(_vm.RefreshCommand);

            case CommandIds.SelectAll:
                return SelectAllInFocusedList();
            case CommandIds.Open:
                return OpenFocusedItem();
            case CommandIds.ShowDetails:
                return ShowDetailsOfFocusedItem();

            case CommandIds.CopyPath:
                return CopyFocusedPaths(all: false);
            case CommandIds.CopyPathList:
                return CopyFocusedPaths(all: true);

            default:
                return false;
        }
    }

    private static bool Run(ICommand command)
    {
        if (!command.CanExecute(null)) return false;
        command.Execute(null);
        return true;
    }

    /// <summary>
    /// "현재 화면 갱신": 그 탭이 자기 화면을 다시 만드는 동작. 전체를 다시 스캔하는 "강제 재스캔" 과 구분한다.
    /// 큰 파일 / 정리 추천은 실제 파일 상태와 대조하는 자체 새로고침이 있고, 나머지는 필터·정렬을 다시 적용해 목록을 다시 그린다.
    /// </summary>
    private bool RefreshCurrentScreen()
    {
        switch ((LiveTab)Tabs.SelectedIndex)
        {
            case LiveTab.LargeFiles:
                return Run(_vm.RefreshLargeFilesCommand);
            case LiveTab.Cleanup:
                return Run(_vm.Cleanup.RefreshCommand);
            case LiveTab.QuickMove:
                return false;   // 빠른 이동의 활성 패널 새로고침은 QuickMoveView 가 먼저 처리한다.
            case LiveTab.Folder:
            case LiveTab.Treemap:
                return Run(_vm.RefreshFolderCommand);   // 지금 폴더만 실제 상태로 맞춘다(드라이브 전체 재스캔은 강제 재스캔)
            default:
                return Run(_vm.ApplyFilterCommand);
        }
    }

    /// <summary>검색 중이거나 검색창에 포커스가 있으면 검색을 지운다. 아니면 목록의 선택을 지운다.</summary>
    private bool CancelSearchOrSelection()
    {
        if (_vm.IsSearchMode || SearchBox.IsKeyboardFocusWithin)
        {
            _vm.ClearSearchCommand.Execute(null);
            if (SearchBox.IsKeyboardFocusWithin) FolderList.Focus();
            return true;
        }

        if (FocusedList() is { SelectedItems.Count: > 0 } list)
        {
            list.SelectedItems.Clear();
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ 포커스가 있는 목록 기준 동작

    private ListView? FocusedList()
    {
        for (var node = Keyboard.FocusedElement as DependencyObject; node != null; node = ParentOf(node))
            if (node is ListView list) return list;
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private bool SelectAllInFocusedList()
    {
        if (FocusedList() is not { } list || list.SelectionMode == SelectionMode.Single) return false;
        list.SelectAll();
        return true;
    }

    private static string? PathOf(object? item) => item switch
    {
        EntryRow row => row.FullPath,
        FsEntry entry => entry.FullPath,
        CleanupCandidate c => c.FullPath,
        _ => null,
    };

    /// <summary>
    /// 행 자체(또는 목록)에 포커스가 있을 때만 그 목록. 행 안의 체크박스·버튼에 포커스가 있으면 null —
    /// Space / Enter 는 그 컨트롤의 기본 동작(체크 토글, 클릭)이 우선이다.
    /// </summary>
    private ListView? FocusedListAtRowLevel()
        => Keyboard.FocusedElement is ListViewItem or ListView ? FocusedList() : null;

    private bool OpenFocusedItem()
    {
        var list = FocusedListAtRowLevel();
        if (list == null || list.SelectedItems.Count != 1) return false;

        if (list.SelectedItem is EntryRow row)
        {
            if (row.IsDirectory)
            {
                EnsureFolderListVisible();
                _vm.Navigate(row.Id);
            }
            else
            {
                ShellService.OpenInExplorer(row.FullPath, false);
            }
            return true;
        }

        if (list.SelectedItem is CleanupCandidate c)
        {
            ShellService.OpenInExplorer(c.FullPath, c.IsDirectory);
            return true;
        }
        return false;
    }

    private bool ShowDetailsOfFocusedItem()
    {
        string? path = PathOf(FocusedListAtRowLevel()?.SelectedItem);
        if (path == null) return false;
        ShellService.ShowProperties(path);
        return true;
    }

    private bool CopyFocusedPaths(bool all)
    {
        if (FocusedList() is not { } list) return false;

        var paths = list.SelectedItems.Cast<object>().Select(PathOf).OfType<string>().ToList();
        if (paths.Count == 0) return false;

        TrySetClipboard(all ? string.Join(Environment.NewLine, paths) : paths[0]);
        if (all) _vm.StatusMessage = $"경로 {paths.Count:N0}개를 복사했습니다.";
        return true;
    }
}
