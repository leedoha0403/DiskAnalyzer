namespace DiskAnalyzer.Core.QuickMove;

/// <summary>이동 대기열의 한 항목 = 엔진이 처리하는 작업 하나.</summary>
public sealed class MoveRequest
{
    public required string SourcePath { get; init; }
    public required string DestDirectory { get; init; }
    public bool IsDirectory { get; init; }

    /// <summary>총 용량(폴더는 하위 전체). 계산되기 전에는 0.</summary>
    public long Size { get; set; }

    /// <summary>파일 개수(파일은 1, 폴더는 하위 파일 수).</summary>
    public long FileCount { get; set; } = 1;

    public string Name
    {
        get
        {
            string trimmed = SourcePath.TrimEnd('\\', '/');
            int i = trimmed.LastIndexOf('\\');
            return i >= 0 ? trimmed[(i + 1)..] : trimmed;
        }
    }

    public string TargetPath => Path.Combine(DestDirectory, Name);
}

/// <summary>대상 위치에 같은 이름이 이미 있을 때 처리하는 방법.</summary>
public enum ConflictChoice { Overwrite, Skip, Rename }

/// <summary>전체 충돌 정책. Ask = 항목마다 물어본다(기본값, 가장 안전).</summary>
public enum ConflictPolicy { Ask, Overwrite, Skip, Rename }

public readonly record struct ConflictDecision(ConflictChoice Choice, bool ApplyToAll);

public sealed class ConflictInfo
{
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
    public bool SourceIsDirectory { get; init; }
    public bool ExistingIsDirectory { get; init; }
    public long SourceSize { get; init; } = -1;
    public long ExistingSize { get; init; } = -1;
    public DateTime SourceModified { get; init; }
    public DateTime ExistingModified { get; init; }

    /// <summary>파일과 폴더처럼 형식이 다르면 덮어쓸 수 없다.</summary>
    public bool CanOverwrite { get; init; } = true;

    public string Name => Path.GetFileName(TargetPath);
}

public enum MoveStatus
{
    Moved,
    Skipped,
    Failed,
    /// <summary>취소되었거나(사용자) 드라이브 연결이 끊겨 시작하지 못한 항목.</summary>
    NotProcessed,
}

public sealed class MoveItemResult
{
    public required MoveRequest Request { get; init; }
    public MoveStatus Status { get; set; }
    public string? Error { get; set; }

    /// <summary>"일부 항목 건너뜀 2개" 같은 보충 설명.</summary>
    public string? Note { get; set; }

    /// <summary>실제로 놓인 경로("새 이름" 을 골랐다면 요청과 다르다).</summary>
    public string FinalPath { get; set; } = string.Empty;

    public bool IsFinished => Status is MoveStatus.Moved or MoveStatus.Skipped;
}

public sealed class MoveSummary
{
    public IReadOnlyList<MoveItemResult> Results { get; init; } = Array.Empty<MoveItemResult>();
    public TimeSpan Elapsed { get; init; }
    public bool WasCancelled { get; init; }

    /// <summary>대상(또는 원본) 드라이브 연결이 끊겨 작업을 중단했다.</summary>
    public bool DriveLost { get; init; }

    public int Moved => Results.Count(r => r.Status == MoveStatus.Moved);
    public int Skipped => Results.Count(r => r.Status == MoveStatus.Skipped);
    public int Failed => Results.Count(r => r.Status == MoveStatus.Failed);
    public int NotProcessed => Results.Count(r => r.Status == MoveStatus.NotProcessed);
    public long BytesMoved => Results.Where(r => r.Status == MoveStatus.Moved).Sum(r => r.Request.Size);
    public long FilesMoved => Results.Where(r => r.Status == MoveStatus.Moved).Sum(r => r.Request.FileCount);

    public bool AllSucceeded => Failed == 0 && NotProcessed == 0;
}

public sealed class MoveProgress
{
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public long FilesDone { get; init; }
    public long FilesTotal { get; init; }
    public int ItemIndex { get; init; }
    public int ItemCount { get; init; }
    public string CurrentName { get; init; } = string.Empty;
    public string CurrentFrom { get; init; } = string.Empty;
    public string CurrentTo { get; init; } = string.Empty;

    public double Fraction => BytesTotal > 0 ? Math.Min(1d, (double)BytesDone / BytesTotal)
                            : FilesTotal > 0 ? Math.Min(1d, (double)FilesDone / FilesTotal) : 0d;
}

public sealed class MoveOptions
{
    public ConflictPolicy Policy { get; set; } = ConflictPolicy.Ask;

    /// <summary>Policy 가 Ask 일 때 백그라운드 스레드에서 호출된다. UI 는 여기서 대화 상자를 띄우고 기다린다.</summary>
    public Func<ConflictInfo, ConflictDecision>? ResolveConflict { get; set; }

    public IProgress<MoveProgress>? Progress { get; set; }

    /// <summary>테스트 전용: 같은 볼륨이어도 이름 바꾸기를 건너뛰고 복사 후 삭제 경로로 처리한다(다른 드라이브 이동을 재현).</summary>
    internal bool ForceCopy { get; set; }
}
