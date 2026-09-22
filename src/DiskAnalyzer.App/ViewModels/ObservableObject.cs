using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace DiskAnalyzer.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute()) { }

    /// <summary>
    /// 언제 CanExecute 를 다시 물어볼지.
    ///
    /// <para>자체 이벤트만 두었더니 <b>아무도 깨우지 않는 커맨드는 영원히 꺼진 채로</b> 남았다 —
    /// 예를 들어 [루트] 는 조건이 "스캔 결과가 있는가" 인데, 앱이 뜰 때 한 번 false 로 평가된 뒤
    /// 스캔이 끝나도 다시 물어보지 않아 버튼이 계속 죽어 있었다. 커맨드마다 잊지 않고
    /// <c>RaiseCanExecuteChanged</c> 를 부르게 하는 것은 규율에 기대는 방식이라 언젠가 또 빠진다.</para>
    ///
    /// <para>WPF 의 <see cref="CommandManager.RequerySuggested"/> 에 얹으면 포커스 · 입력처럼
    /// 상태가 바뀌었을 법한 순간마다 알아서 다시 물어본다. 커맨드가 수십 개 수준이라 비용은 무시할 만하다.</para>
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>지금 당장 다시 물어보게 한다. UI 스레드가 아니면 넘겨서 부른다.</summary>
    public void RaiseCanExecuteChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) CommandManager.InvalidateRequerySuggested();
        else dispatcher.BeginInvoke(CommandManager.InvalidateRequerySuggested);
    }
}

public sealed class BreadcrumbItem
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public bool IsLast { get; init; }
}
