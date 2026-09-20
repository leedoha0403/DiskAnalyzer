using System.Windows;
using System.Windows.Input;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.QuickMove;

namespace DiskAnalyzer.App.Views;

/// <summary>
/// 이동 중 이름이 같은 항목을 만났을 때 하나씩 보여 주고 고르게 한다.
/// 기본 버튼은 "건너뛰기"(아무것도 덮어쓰지 않는 쪽)이고, 창을 그냥 닫아도 건너뛴다.
/// </summary>
public partial class MoveConflictWindow : Window
{
    public ConflictDecision Decision { get; private set; } = new(ConflictChoice.Skip, false);

    public MoveConflictWindow(ConflictInfo info)
    {
        InitializeComponent();

        NameText.Text = info.Name;
        NameText.ToolTip = info.TargetPath;

        bool folders = info.SourceIsDirectory && info.ExistingIsDirectory;
        ExistingSize.Text = info.ExistingIsDirectory ? "폴더" : SizeFormatter.Format(info.ExistingSize);
        SourceSize.Text = info.SourceIsDirectory ? "폴더" : SizeFormatter.Format(info.SourceSize);
        ExistingDate.Text = FormatDate(info.ExistingModified);
        SourceDate.Text = FormatDate(info.SourceModified);
        ExistingPath.Text = info.TargetPath;
        SourcePath.Text = info.SourcePath;
        ExistingPath.ToolTip = info.TargetPath;
        SourcePath.ToolTip = info.SourcePath;

        if (folders)
        {
            OverwriteButton.Content = "병합";
            HintText.Text = "같은 이름의 폴더가 있습니다. 병합하면 폴더 안의 항목을 합치고, 안쪽에서 이름이 같은 파일은 다시 물어봅니다.";
        }
        else if (!info.CanOverwrite)
        {
            OverwriteButton.Content = "덮어쓰기";
            OverwriteButton.IsEnabled = false;
            HintText.Text = "파일과 폴더는 서로 덮어쓸 수 없습니다. 건너뛰거나 새 이름으로 옮기세요.";
        }
        else
        {
            OverwriteButton.Content = "덮어쓰기";
            HintText.Text = Compare(info);
        }
    }

    private static string FormatDate(DateTime t) => t == default ? string.Empty : t.ToString("yyyy-MM-dd HH:mm");

    /// <summary>어느 쪽이 더 새로운지 / 큰지 한 줄로 알려 준다. 덮어쓰기 판단에 가장 도움이 되는 정보다.</summary>
    private static string Compare(ConflictInfo i)
    {
        var parts = new List<string>();
        if (i.SourceModified != default && i.ExistingModified != default)
        {
            if (i.SourceModified > i.ExistingModified) parts.Add("이동할 파일이 더 최근입니다");
            else if (i.SourceModified < i.ExistingModified) parts.Add("기존 파일이 더 최근입니다");
            else parts.Add("수정 시각이 같습니다");
        }
        if (i.SourceSize >= 0 && i.ExistingSize >= 0)
        {
            if (i.SourceSize > i.ExistingSize) parts.Add("이동할 파일이 더 큽니다");
            else if (i.SourceSize < i.ExistingSize) parts.Add("기존 파일이 더 큽니다");
            else parts.Add("크기가 같습니다");
        }
        return string.Join(" · ", parts);
    }

    private void Choose(ConflictChoice choice)
    {
        Decision = new ConflictDecision(choice, ApplyAll.IsChecked == true);
        Close();
    }

    private void OnOverwrite(object sender, RoutedEventArgs e) => Choose(ConflictChoice.Overwrite);
    private void OnSkip(object sender, RoutedEventArgs e) => Choose(ConflictChoice.Skip);
    private void OnRename(object sender, RoutedEventArgs e) => Choose(ConflictChoice.Rename);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Choose(ConflictChoice.Skip);
    }
}
