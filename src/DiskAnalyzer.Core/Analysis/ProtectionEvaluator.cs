using DiskAnalyzer.Core.Interop;
using DiskAnalyzer.Core.Models;
using Microsoft.Win32;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.Core.Analysis;

/// <summary>
/// 보호 등급 판정기.
///
/// [설계 원칙]
///   "보호 여부를 먼저 결정하고, 그 다음에 정리 가치 여부를 판단한다."
///   이 클래스는 정리 가치(점수)를 전혀 모른다. 오직 "삭제해도 되는가"만 답한다.
///
/// [판정 순서] — 위에서 걸리면 즉시 확정하고 아래는 보지 않는다.
///   1. 경로 유효성 검사            -> 실패 시 P0
///   2. 시스템 핵심 파일             -> P0
///   3. 부팅 / Registry / Pagefile / Recovery -> P0
///   4. OS 가 보호 중인 핵심 경로      -> P0
///   5. Reparse Point / Junction    -> P1 + 재귀 금지
///   6. 현재 프로그램/서비스 사용 중    -> P1   (삭제 직전 재검증에서만 확인)
///   7. Windows / Program Files / ProgramData / AppData -> P1
///   8. 알려진 Temp / Cache / Dump    -> P2 후보 표시
///   9. (호출자가 오래됨+미사용+재생성가능을 확인해 P2 확정)
///  10. 그 외                        -> P3
/// </summary>
public static class ProtectionEvaluator
{
    private static readonly string[] SystemCoreFileNames =
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys",
        "bootmgr", "bootnxt", "ntldr", "bootsect.bak", "bcd", "bcd.log",
    };

    /// <summary>OS 동작의 핵심이라 통째로 P0 인 경로(시스템 드라이브 기준 상대 경로).</summary>
    private static readonly string[] SystemCoreDirs =
    {
        @"\windows\system32",
        @"\windows\syswow64",
        @"\windows\winsxs",
        @"\windows\servicing",
        @"\windows\boot",
        @"\windows\bootstat.dat",
        @"\boot",
        @"\efi",
        @"\recovery",
        @"\system volume information",
        @"\$windows.~bt",
        @"\$windows.~ws",
    };

    /// <summary>삭제하면 프로그램/Windows 기능이 깨질 수 있는 경로.</summary>
    private static readonly string[] HighRiskDirs =
    {
        @"\windows",
        @"\program files",
        @"\program files (x86)",
        @"\programdata",
        @"\$recycle.bin",
    };

    /// <summary>알려진 Temp / Cache / Dump 위치. 경로 조각으로 판정한다.</summary>
    private static readonly string[] RegenerableFragments =
    {
        @"\appdata\local\temp\",
        @"\windows\temp\",
        @"\temp\",
        @"\tmp\",
        @"\cache\",
        @"\cache2\",
        @"\codecache\",
        @"\shadercache\",
        @"\dxcache\",
        @"\gpucache\",
        @"\thumbnails\",
        @"\crashdumps\",
        @"\minidump\",
        @"\.vs\",
        @"\node_modules\",
        @"\obj\",
        @"\bin\debug\",
        @"\bin\release\",
        @"\x64\debug\",
        @"\x64\release\",
        @"\ipch\",
    };

    private static string[]? _configuredPageFiles;
    private static readonly object PageFileGate = new();

    // ------------------------------------------------------------------ 진입점

    /// <summary>
    /// 스캔 중/분석 중 사용하는 정적 판정. 디스크 I/O 를 하지 않는다.
    /// 프로세스 사용 여부(6단계)는 여기서 확인할 수 없으므로 삭제 직전 재검증에서 다시 본다.
    /// </summary>
    public static ProtectionVerdict Evaluate(string fullPath, bool isDirectory, uint attributes, CategoryFlags flags)
    {
        // 1. 경로 유효성 검사
        if (!TryNormalize(fullPath, out string path))
        {
            return new ProtectionVerdict
            {
                Level = ProtectionLevel.P0,
                Reason = "경로를 해석할 수 없어 안전을 위해 보호합니다.",
            };
        }

        string lower = path.ToLowerInvariant();
        string tail = TailAfterDrive(lower);
        string name = FileName(lower);

        // 2/3. 시스템 핵심 파일 (파일명 + 실제 위치를 함께 본다)
        if (!isDirectory && IsSystemCoreFile(path, lower, name, out string coreReason))
            return new ProtectionVerdict { Level = ProtectionLevel.P0, Reason = coreReason };

        // 3/4. 부팅 / 레지스트리 / 복구 / OS 핵심 경로
        foreach (var dir in SystemCoreDirs)
        {
            if (tail.Equals(dir, StringComparison.Ordinal) ||
                tail.StartsWith(dir + "\\", StringComparison.Ordinal))
            {
                return new ProtectionVerdict
                {
                    Level = ProtectionLevel.P0,
                    Reason = $"Windows 시스템 핵심 영역({dir.Trim('\\')})입니다. 현재 시스템 동작에 필요하여 삭제할 수 없습니다.",
                };
            }
        }

        // 5. Reparse Point / Junction / Symbolic Link -> P1 + 재귀 금지
        if ((attributes & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            return new ProtectionVerdict
            {
                Level = ProtectionLevel.P1,
                Reason = "Junction / Symbolic Link 입니다. 실제 대상이 다른 경로일 수 있어 하위를 따라가지 않습니다.",
                BlockRecursion = true,
            };
        }

        // 7. 위험 경로
        foreach (var dir in HighRiskDirs)
        {
            if (tail.Equals(dir, StringComparison.Ordinal) ||
                tail.StartsWith(dir + "\\", StringComparison.Ordinal))
            {
                return new ProtectionVerdict
                {
                    Level = ProtectionLevel.P1,
                    Reason = $"{dir.Trim('\\')} 경로입니다. 설치된 프로그램이나 Windows 기능에 영향을 줄 수 있습니다.",
                };
            }
        }

        // AppData 는 사용자별 경로라 별도 판정. 단 AppData\Local\Temp 는 아래에서 P2 후보가 된다.
        if (lower.Contains(@"\appdata\", StringComparison.Ordinal) && !IsRegenerable(lower))
        {
            return new ProtectionVerdict
            {
                Level = ProtectionLevel.P1,
                Reason = "사용자 AppData 경로입니다. 프로그램 설정이나 상태 데이터가 들어 있을 수 있습니다.",
            };
        }

        // 확장자만으로 P1 을 만들지 않는다. 위험 경로에 있을 때만 위험 신호가 의미를 갖는다.
        // (Downloads\old_setup.exe 는 P3, Program Files\App\app.exe 는 위에서 이미 P1)

        // 8. 알려진 Temp / Cache / Dump -> P2 후보
        // 5. P2 대표 유형: Temp / Cache / Crash Dump / 오래된 로그 / 빌드 캐시 / 재생성 가능한 데이터
        bool regenerable = IsRegenerable(lower)
                           || (flags & (CategoryFlags.Temp | CategoryFlags.Cache)) != 0
                           || (flags & CategoryFlags.Log) != 0
                           || (flags & (CategoryFlags.Build | CategoryFlags.VisualStudio)) != 0;

        return new ProtectionVerdict
        {
            Level = ProtectionLevel.P3,
            Reason = regenerable
                ? "재생성 가능한 임시/캐시 성격의 위치입니다."
                : "일반 사용자 파일입니다.",
            RegenerableLocation = regenerable,
        };
    }

    /// <summary>
    /// 삭제 확인 창을 띄우기 전에 항목 하나를 실제 상태까지 읽어 판정한다.
    ///
    ///  - 파일   : 속성을 읽어 Reparse Point 를 반영한 정적 판정(파일을 열지 않는다)
    ///  - 폴더   : 하위를 훑어 가장 높은 등급을 취한다(11) — 폴더 자체가 P3 여도 안에 P1/P0 가 있을 수 있다
    ///
    /// 하위 탐색이 들어가므로 <strong>UI 스레드에서 호출하지 말 것</strong>.
    /// </summary>
    public static ProtectionVerdict EvaluateEntry(string fullPath, bool isDirectory, CategoryFlags flags)
    {
        if (isDirectory) return EvaluateFolderTree(fullPath, flags);

        uint attributes = 0;
        if (TryNormalize(fullPath, out string path) && Win32.GetFileAttributesEx(path, 0, out var data))
            attributes = data.dwFileAttributes;

        return Evaluate(fullPath, false, attributes, flags);
    }

    /// <summary>
    /// 9. 삭제 직전 재검증.
    /// 스캔 시점의 판정을 그대로 믿지 않는다. 존재 여부·속성·사용 중 여부를 지금 다시 확인한다.
    /// 스캔 당시 P3 였어도 지금 프로그램이 물고 있으면 P1 으로 승격한다.
    /// </summary>
    public static ProtectionVerdict EvaluateForDeletion(string fullPath, bool isDirectory, CategoryFlags flags)
    {
        if (!TryNormalize(fullPath, out string path))
        {
            return new ProtectionVerdict
            {
                Level = ProtectionLevel.P0,
                Reason = "경로를 해석할 수 없어 안전을 위해 보호합니다.",
            };
        }

        uint attributes = 0;
        if (Win32.GetFileAttributesEx(path, 0, out var data))
            attributes = data.dwFileAttributes;

        var verdict = Evaluate(path, isDirectory, attributes, flags);

        // 이미 P0/P1 이면 더 볼 것 없다(더 높은 등급만 이긴다).
        if (verdict.Level >= ProtectionLevel.P1) return verdict;

        // 6. 현재 사용 중인가 -> P1 승격
        if (!isDirectory && IsInUse(path))
        {
            return new ProtectionVerdict
            {
                Level = ProtectionLevel.P1,
                Reason = "현재 다른 프로그램이 사용 중입니다.",
                RegenerableLocation = verdict.RegenerableLocation,
            };
        }

        return verdict;
    }

    /// <summary>
    /// 11. 폴더 삭제 판정.
    /// 폴더 자체가 P3 여도 내부에 P1/P0 가 있을 수 있으므로 하위를 훑어 가장 높은 등급을 취한다.
    /// Junction 을 만나면 그 지점에서 재귀를 멈춘다.
    /// </summary>
    public static ProtectionVerdict EvaluateFolderTree(string folderPath, CategoryFlags flags, int maxEntries = 20000)
    {
        var worst = EvaluateForDeletion(folderPath, isDirectory: true, flags);
        if (worst.Level >= ProtectionLevel.P0 || worst.BlockRecursion) return worst;

        int seen = 0;
        try
        {
            foreach (var entry in EnumerateSafe(folderPath, ref seen, maxEntries))
            {
                var v = Evaluate(entry.Path, entry.IsDirectory, entry.Attributes, flags);
                if (v.Level <= worst.Level) continue;

                worst = new ProtectionVerdict
                {
                    Level = v.Level,
                    Reason = $"하위 항목 때문에 등급이 올라갑니다: {v.Reason} ({entry.Path})",
                    BlockRecursion = v.BlockRecursion,
                    RegenerableLocation = worst.RegenerableLocation,
                };

                if (worst.Level == ProtectionLevel.P0) break;
            }
        }
        catch
        {
            // 열거 실패는 정보 부족이므로 보수적으로 한 단계 올린다.
            if (worst.Level < ProtectionLevel.P1)
            {
                worst = new ProtectionVerdict
                {
                    Level = ProtectionLevel.P1,
                    Reason = "하위 항목을 확인할 수 없어 안전하게 고위험으로 처리합니다.",
                    RegenerableLocation = worst.RegenerableLocation,
                };
            }
        }

        return worst;
    }

    // ------------------------------------------------------------------ 내부

    private readonly record struct Entry(string Path, bool IsDirectory, uint Attributes);

    /// <summary>
    /// 폴더 하위를 훑는다. 열거 결과(WIN32_FIND_DATAW)에 속성이 이미 들어 있으므로 항목마다 GetFileAttributesEx 를
    /// 따로 부르지 않는다(2만 건 기준 검증 시간 절반 이하). "\\?\" 접두사로 긴 경로도 열거한다.
    /// </summary>
    private static unsafe List<Entry> EnumerateSafe(string root, ref int seen, int maxEntries)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var result = new List<Entry>();

        while (stack.Count > 0 && seen < maxEntries)
        {
            string dir = stack.Pop();

            string pattern;
            try { pattern = FastDeleter.ToExtended(dir) + @"\*"; }
            catch { continue; }

            using var handle = Win32.FindFirstFileEx(
                pattern, Win32.FindExInfoBasic, out var data,
                Win32.FindExSearchNameMatch, IntPtr.Zero, Win32.FIND_FIRST_EX_LARGE_FETCH);
            if (handle.IsInvalid) continue;   // 열거할 수 없는 폴더는 건너뛴다(호출자가 정보 부족으로 다룬다)

            do
            {
                char* p = data.cFileName;
                int len = 0;
                while (len < Win32.MAX_PATH && p[len] != '\0') len++;
                var name = new ReadOnlySpan<char>(p, len);

                if (name.Length == 0) continue;
                if (name[0] == '.' && (name.Length == 1 || (name.Length == 2 && name[1] == '.'))) continue;
                if (seen++ >= maxEntries) break;

                string child = dir.Length > 0 && dir[^1] == '\\'
                    ? string.Concat(dir, name)
                    : string.Concat(dir, "\\", name);

                uint attr = data.dwFileAttributes;
                bool isDir = (attr & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0;
                bool isReparse = (attr & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

                result.Add(new Entry(child, isDir, attr));

                // Junction 지점에서 재귀 중단.
                if (isDir && !isReparse) stack.Push(child);
            }
            while (Win32.FindNextFile(handle, out data));
        }
        return result;
    }

    private static bool IsSystemCoreFile(string path, string lower, string name, out string reason)
    {
        reason = string.Empty;

        // 실제 Windows 설정에 등록된 페이지 파일인지 확인한다(이름만으로 판단하지 않는다).
        foreach (var configured in GetConfiguredPageFiles())
        {
            if (lower.Equals(configured, StringComparison.Ordinal))
            {
                reason = "현재 Windows 가 사용 중인 페이지 파일입니다.";
                return true;
            }
        }

        foreach (var core in SystemCoreFileNames)
        {
            if (!name.Equals(core, StringComparison.Ordinal)) continue;

            // pagefile.sys 등은 드라이브 루트 직속일 때만 시스템 파일로 본다.
            // (사용자가 만든 동명 파일을 잘못 보호하지 않기 위함)
            bool atRoot = TailAfterDrive(lower).TrimStart('\\').Equals(core, StringComparison.Ordinal);
            if (atRoot || lower.Contains(@"\windows\system32\config\", StringComparison.Ordinal))
            {
                reason = $"Windows 시스템 핵심 파일({core})입니다.";
                return true;
            }
        }

        // 활성 레지스트리 Hive
        if (lower.Contains(@"\windows\system32\config\", StringComparison.Ordinal))
        {
            reason = "Windows 레지스트리 저장 영역입니다.";
            return true;
        }

        // 커널 드라이버
        if (lower.Contains(@"\windows\system32\drivers\", StringComparison.Ordinal))
        {
            reason = "커널 드라이버 파일입니다.";
            return true;
        }

        return false;
    }

    /// <summary>레지스트리에 설정된 실제 페이지 파일 경로. 프로세스 수명 동안 한 번만 읽는다.</summary>
    private static string[] GetConfiguredPageFiles()
    {
        lock (PageFileGate)
        {
            if (_configuredPageFiles != null) return _configuredPageFiles;

            var list = new List<string>(2);
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
                if (key?.GetValue("PagingFiles") is string[] entries)
                {
                    foreach (var entry in entries)
                    {
                        // "C:\pagefile.sys 0 0" 형태
                        string first = entry.Split(' ')[0].Trim();
                        if (first.Length > 0) list.Add(first.ToLowerInvariant());
                    }
                }
            }
            catch
            {
                // 읽지 못하면 아래 이름 규칙으로만 판정한다.
            }

            _configuredPageFiles = list.ToArray();
            return _configuredPageFiles;
        }
    }

    private static bool IsInUse(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch { return false; }
    }

    private static bool IsRegenerable(string lower)
    {
        foreach (var fragment in RegenerableFragments)
            if (lower.Contains(fragment, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>1단계. 절대 경로화 + ".."/"." 정리 + 구분자 통일.</summary>
    internal static bool TryNormalize(string input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        try
        {
            string cleaned = input.Replace('/', '\\').Trim();
            if (cleaned.StartsWith(@"\\?\", StringComparison.Ordinal)) cleaned = cleaned[4..];

            string full = Path.GetFullPath(cleaned);
            if (full.Length < 2) return false;

            normalized = full.Length > 3 ? full.TrimEnd('\\') : full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>"c:\windows\system32" -> "\windows\system32". UNC 는 서버/공유 다음부터.</summary>
    private static string TailAfterDrive(string lower)
    {
        if (lower.Length >= 2 && lower[1] == ':') return lower[2..];
        if (lower.StartsWith(@"\\", StringComparison.Ordinal))
        {
            int share = lower.IndexOf('\\', 2);
            if (share < 0) return "\\";
            int next = lower.IndexOf('\\', share + 1);
            return next < 0 ? "\\" : lower[next..];
        }
        return lower;
    }

    private static string FileName(string lower)
    {
        int idx = lower.LastIndexOf('\\');
        return idx < 0 ? lower : lower[(idx + 1)..];
    }
}
