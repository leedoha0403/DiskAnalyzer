using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.Views;

/// <summary>"무시 목록" 버튼을 눌렀을 때 뜨는 작은 메뉴 - 항목을 누르면 그 이름의 무시를 푼다.</summary>
internal static class ProcessIgnoreMenu
{
    public static void Show(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top };
        var names = ProcessIgnoreList.Names;

        if (names.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "무시한 프로세스가 없습니다", IsEnabled = false });
        }
        else
        {
            menu.Items.Add(new MenuItem { Header = "누르면 무시를 해제합니다", IsEnabled = false });
            menu.Items.Add(new Separator());
            foreach (var name in names)
            {
                var item = new MenuItem { Header = $"✕  {name}" };
                string captured = name;
                item.Click += (_, _) => ProcessIgnoreList.Remove(captured);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var all = new MenuItem { Header = "모두 해제" };
            all.Click += (_, _) => ProcessIgnoreList.Clear();
            menu.Items.Add(all);
        }

        menu.IsOpen = true;
    }
}
