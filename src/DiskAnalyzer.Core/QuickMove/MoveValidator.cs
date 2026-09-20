using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Interop;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.QuickMove;

public enum IssueSeverity { Warning, Error }

public sealed class MoveIssue
{
    public required IssueSeverity Severity { get; init; }
    public required string Message { get; init; }

    /// <summary>문제가 된 항목. 대상 폴더 전체에 대한 문제면 null.</summary>
    public MoveRequest? Request { get; init; }
}

/// <summary>대상 드라이브 하나의 공간 상태.</summary>
public sealed class SpaceRow
{
    public required string Drive { get; init; }
    public long Free { get; init; }

    /// <summary>실제로 복사해야 하는 용량. 같은 볼륨 안의 이동은 데이터를 옮기지 않으므로 0.</summary>
    public long Required { get; init; }

    /// <summary>같은 볼륨 이동(추정)이라 공간을 쓰지 않는 용량.</summary>
    public long InPlace { get; init; }

    public bool FreeKnown => Free >= 0;
    public bool Enough => !FreeKnown || Required <= Free;
    public long After => FreeKnown ? Free - Required : -1;
}

/// <summary>
/// 이동을 시작하기 전에 하는 검사.
///  - 대기열에 넣는 순간(<see cref="CheckAdd"/>): 값싼 문자열 검사만. 즉시 거절할 수 있는 것.
///  - 이동 시작 직전(<see cref="Validate"/>): 실제 파일 시스템을 본다(존재 / 권한 / 사용 중 / 보호 등급). 백그라운드에서 호출한다.
///  - 공간(<see cref="CheckSpace"/>): 대기열이 바뀔 때마다 화면이 갱신한다.
/// 보호 등급은 기존 <see cref="ProtectionEvaluator"/> 정책을 그대로 쓴다(P0 이동 금지 / P1 강한 경고).
/// </summary>
public static class MoveValidator
{
    public const string SameLocationFile = "이미 같은 위치에 있는 파일입니다.";
    public const string SameLocationFolder = "이미 같은 위치에 있는 폴더입니다.";
    public const string IntoSelf = "폴더를 자신의 하위 폴더로 이동할 수 없습니다.";

    /// <summary>대기열에 넣을 수 있는가. 안 되면 사용자에게 그대로 보여 줄 문구를 돌려준다.</summary>
    public static bool CheckAdd(string source, bool isDirectory, string destDirectory, out string? error)
    {
        error = null;

        if (!PathUtil.TryNormalize(source, out string src) || !PathUtil.TryNormalize(destDirectory, out string dst))
        {
            error = "경로를 해석할 수 없습니다.";
            return false;
        }

        // 동일 경로 이동 금지: C:\Work\a.txt → C:\Work
        string? parent = PathUtil.Parent(src);
        if (parent != null && PathUtil.Equal(parent, dst))
        {
            error = isDirectory ? SameLocationFolder : SameLocationFile;
            return false;
        }

        // 하위 폴더 이동 금지: C:\Work → C:\Work\Backup (자기 자신 포함)
        if (isDirectory && PathUtil.IsSameOrUnder(src, dst))
        {
            error = IntoSelf;
            return false;
        }

        if (parent == null)
        {
            error = "드라이브 전체는 이동할 수 없습니다.";
            return false;
        }

        return true;
    }

    /// <summary>이동 직전 검사. 디스크를 읽으므로 UI 스레드에서 부르지 말 것.</summary>
    public static IReadOnlyList<MoveIssue> Validate(IReadOnlyList<MoveRequest> requests, CancellationToken ct = default)
    {
        var issues = new List<MoveIssue>();
        var destChecked = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var req in requests)
        {
            ct.ThrowIfCancellationRequested();

            bool? isDir = PathUtil.IsDirectory(req.SourcePath);
            if (isDir == null)
            {
                issues.Add(Error(req, "원본을 찾을 수 없습니다. 이미 이동되었거나 삭제되었을 수 있습니다."));
                continue;
            }

            if (!CheckAdd(req.SourcePath, isDir.Value, req.DestDirectory, out string? addError))
            {
                issues.Add(Error(req, addError!));
                continue;
            }

            if (!Directory.Exists(req.DestDirectory))
            {
                issues.Add(Error(req, "대상 폴더가 없습니다."));
                continue;
            }

            // 대상 폴더는 한 번만 본다(같은 폴더로 여러 항목을 보내는 경우가 대부분이다).
            string destKey = PathUtil.Normalize(req.DestDirectory);
            if (!destChecked.TryGetValue(destKey, out bool destOk))
            {
                destOk = CheckDestination(destKey, issues);
                destChecked[destKey] = destOk;
            }
            if (!destOk)
            {
                issues.Add(Error(req, "대상 폴더에 쓸 수 없어 이 항목을 옮길 수 없습니다."));
                continue;
            }

            // 보호 등급. 폴더는 안쪽까지 훑어 가장 높은 등급을 본다(ProtectionEvaluator 가 상한을 둔다).
            var verdict = isDir.Value
                ? ProtectionEvaluator.EvaluateFolderTree(req.SourcePath, CategoryFlags.None)
                : ProtectionEvaluator.EvaluateForDeletion(req.SourcePath, false, CategoryFlags.None);

            switch (verdict.Level)
            {
                case ProtectionLevel.P0:
                    issues.Add(Error(req, "이동할 수 없는 시스템 보호 항목입니다. " + verdict.Reason));
                    break;
                case ProtectionLevel.P1:
                    issues.Add(new MoveIssue
                    {
                        Severity = IssueSeverity.Warning,
                        Request = req,
                        Message = "고위험 항목입니다. 옮기면 프로그램이나 Windows 가 정상 동작하지 않을 수 있습니다. " + verdict.Reason,
                    });
                    break;
            }
        }

        return issues;
    }

    /// <summary>대상 폴더 자체를 검사한다. 쓸 수 없으면 false. 경고만 있으면 true.</summary>
    private static bool CheckDestination(string dest, List<MoveIssue> issues)
    {
        Win32.GetFileAttributesEx(PathUtil.ToExtendedSafe(dest), 0, out var data);
        var verdict = ProtectionEvaluator.Evaluate(dest, true, data.dwFileAttributes, CategoryFlags.None);

        if (verdict.Level == ProtectionLevel.P0)
        {
            issues.Add(new MoveIssue
            {
                Severity = IssueSeverity.Error,
                Message = $"{dest} 는 이동할 수 없는 시스템 보호 위치입니다. {verdict.Reason}",
            });
            return false;
        }
        if (verdict.Level == ProtectionLevel.P1)
        {
            issues.Add(new MoveIssue
            {
                Severity = IssueSeverity.Warning,
                Message = $"{dest} 는 고위험 위치입니다. {verdict.Reason}",
            });
        }

        // 쓰기 권한: 실제로 임시 파일을 만들었다가 닫으면서 지워 본다. 속성 / ACL 만 보는 것보다 정확하다.
        try
        {
            string probe = Path.Combine(dest, ".diskanalyzer-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            issues.Add(new MoveIssue
            {
                Severity = IssueSeverity.Error,
                Message = $"{dest} 에 쓸 수 없습니다. {ex.Message}",
            });
            return false;
        }
    }

    /// <summary>대상 드라이브별 필요 용량과 여유 공간. 같은 볼륨 안의 이동은 필요 용량 0 으로 본다.</summary>
    public static IReadOnlyList<SpaceRow> CheckSpace(IEnumerable<MoveRequest> requests)
    {
        var groups = requests.GroupBy(r => PathUtil.DriveName(r.DestDirectory), StringComparer.OrdinalIgnoreCase);
        var rows = new List<SpaceRow>();

        foreach (var g in groups)
        {
            long required = 0, inPlace = 0;
            string anyDest = g.First().DestDirectory;
            foreach (var r in g)
            {
                if (PathUtil.SameVolume(r.SourcePath, r.DestDirectory)) inPlace += r.Size;
                else required += r.Size;
            }

            rows.Add(new SpaceRow
            {
                Drive = g.Key,
                Free = PathUtil.GetFreeSpace(anyDest),
                Required = required,
                InPlace = inPlace,
            });
        }
        return rows;
    }

    private static MoveIssue Error(MoveRequest req, string message)
        => new() { Severity = IssueSeverity.Error, Request = req, Message = message };
}
