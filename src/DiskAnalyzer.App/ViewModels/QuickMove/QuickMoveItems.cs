using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.QuickMove;

namespace DiskAnalyzer.App.ViewModels.QuickMove;

/// <summary>"빠른 위치" 버튼 하나. 즐겨찾기 / 기본 폴더(바탕화면·다운로드·문서) / 드라이브.</summary>
public sealed class QuickLocation
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsFavorite { get; init; }
    public string ToolTip => Path;
}

/// <summary>Breadcrumb 조각. 클릭하면 그 폴더로 이동한다.</summary>
public sealed class CrumbItem
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsLast { get; init; }
}

public enum QueueItemState { Waiting, Moving, Done, Skipped, Failed }

/// <summary>이동 대기열의 한 줄. 폴더 크기는 백그라운드에서 계산한다(계산이 끝나기 전에는 "계산 중").</summary>
public sealed class QueueItemViewModel : ObservableObject
{
    private readonly QuickMoveViewModel _owner;
    private readonly CancellationTokenSource _cts = new();

    private MoveRequest _request;
    private bool _measured;
    private QueueItemState _state;
    private string _stateText = string.Empty;

    public QueueItemViewModel(QuickMoveViewModel owner, FsEntry entry, string destDirectory)
    {
        _owner = owner;
        Level = entry.Level;
        Reason = entry.ProtectionReason;

        _request = new MoveRequest
        {
            SourcePath = entry.FullPath,
            DestDirectory = destDirectory,
            IsDirectory = entry.IsDirectory,
            Size = entry.IsDirectory ? 0 : Math.Max(0, entry.Size),
            FileCount = 1,
        };

        if (entry.IsDirectory) StartMeasure();
        else _measured = true;
    }

    public MoveRequest Request => _request;
    public string SourcePath => _request.SourcePath;
    public string DestDirectory => _request.DestDirectory;
    public bool IsDirectory => _request.IsDirectory;
    public string Name => _request.Name;
    public ProtectionLevel Level { get; }
    public string Reason { get; }

    public bool IsMeasured => _measured;
    public long Size => _request.Size;
    public long FileCount => _request.FileCount;

    /// <summary>"4.01 GB" / 폴더는 "640 MB · 파일 1,203개". 계산 중이면 "계산 중…".</summary>
    public string SizeText => _measured ? SizeFormatter.Format(_request.Size) : "계산 중…";

    public string DetailText => _request.IsDirectory && _measured ? $"파일 {_request.FileCount:N0}개" : string.Empty;

    public string FromText => System.IO.Path.GetDirectoryName(_request.SourcePath.TrimEnd('\\')) ?? _request.SourcePath;

    /// <summary>C:\Work\DNF\Bin → D:\Archive\DNF</summary>
    public string RouteText => $"{FromText}  →  {_request.DestDirectory}";

    /// <summary>P1(고위험)은 이유를, 그 밖에는 전체 경로를 보여 준다.</summary>
    public string ToolTipText => Level == ProtectionLevel.P1 && Reason.Length > 0 ? "⚠ " + Reason : RouteText;

    public string Glyph => Level switch
    {
        ProtectionLevel.P1 => "⚠",
        _ => string.Empty,
    };

    /// <summary>같은 드라이브 안의 이동이면 "빠른 이동", 아니면 "복사 후 삭제". 추정이며 실제 방식은 엔진이 정한다.</summary>
    public bool IsSameVolume => PathUtil.SameVolume(_request.SourcePath, _request.DestDirectory);

    public string ModeText => IsSameVolume ? "⚡ 즉시 이동" : $"{PathUtil.DriveName(_request.SourcePath)} → {PathUtil.DriveName(_request.DestDirectory)} 복사 후 삭제";

    public QueueItemState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(IsFailed));
                Raise(nameof(IsMoving));
            }
        }
    }

    public bool IsFailed => _state == QueueItemState.Failed;
    public bool IsMoving => _state == QueueItemState.Moving;

    public string StateText
    {
        get => _stateText;
        set
        {
            if (Set(ref _stateText, value)) Raise(nameof(HasStateText));
        }
    }

    public bool HasStateText => _stateText.Length > 0;

    /// <summary>같은 원본을 다른 목적지로 다시 보내면 목적지만 바꾼다.</summary>
    public void SetDestination(string destDirectory)
    {
        _request = new MoveRequest
        {
            SourcePath = _request.SourcePath,
            DestDirectory = destDirectory,
            IsDirectory = _request.IsDirectory,
            Size = _request.Size,
            FileCount = _request.FileCount,
        };
        Raise(nameof(Request));
        Raise(nameof(DestDirectory));
        Raise(nameof(RouteText));
        Raise(nameof(ModeText));
        Raise(nameof(IsSameVolume));
    }

    public void ResetState()
    {
        State = QueueItemState.Waiting;
    }

    public void Cancel() => _cts.Cancel();

    private void StartMeasure()
    {
        if (_owner.TryGetCachedMeasure(_request.SourcePath, out var cached))
        {
            Apply(cached);
            return;
        }

        var token = _cts.Token;
        string path = _request.SourcePath;
        _ = Task.Run(() =>
        {
            try { return (ok: true, r: TreeMeasure.Measure(path, token)); }
            catch (OperationCanceledException) { return (ok: false, r: default(TreeMeasure.Result)); }
            catch (Exception) { return (ok: true, r: default(TreeMeasure.Result)); }
        }).ContinueWith(t =>
        {
            if (!t.Result.ok || token.IsCancellationRequested) return;
            _owner.CacheMeasure(path, t.Result.r);
            Apply(t.Result.r);
        }, _owner.UiScheduler);
    }

    private void Apply(TreeMeasure.Result r)
    {
        _request.Size = r.Bytes;
        _request.FileCount = r.Files;
        _measured = true;
        Raise(nameof(IsMeasured));
        Raise(nameof(Size));
        Raise(nameof(SizeText));
        Raise(nameof(DetailText));
        _owner.OnQueueSizeChanged();
    }
}

/// <summary>완료 화면의 결과 한 줄.</summary>
public sealed class ResultRowViewModel
{
    public required string Name { get; init; }
    public required string Icon { get; init; }
    public required string Text { get; init; }
    public string Detail { get; init; } = string.Empty;
    public MoveStatus Status { get; init; }
    public bool IsFailed => Status is MoveStatus.Failed or MoveStatus.NotProcessed;
    public string SizeText { get; init; } = string.Empty;
}

/// <summary>대상 드라이브 하나의 "현재 여유 → 이동 후" 표시.</summary>
public sealed class SpaceLineViewModel
{
    public required string Drive { get; init; }
    public required string Text { get; init; }
    public bool IsProblem { get; init; }
}
