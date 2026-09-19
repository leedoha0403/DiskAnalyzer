namespace DiskAnalyzer.Core.Models;

public enum ScanMode
{
    /// <summary>NTFS + 관리자 권한이면 Fast, 아니면 Compatibility 로 자동 결정.</summary>
    Auto = 0,
    /// <summary>NTFS MFT 직접 파싱.</summary>
    Fast = 1,
    /// <summary>FindFirstFileEx 기반 디렉터리 열거.</summary>
    Compatibility = 2,
}

public enum ScanState
{
    Idle,
    Preparing,
    Running,
    Finalizing,
    Completed,
    Cancelled,
    Failed,
}

public enum DriveKind
{
    Unknown,
    Hdd,
    Ssd,
}

/// <summary>
/// 11. 개발 환경 자동 분류 태그. 정보 제공 전용이며 자동 삭제 판단에 사용하지 않는다.
/// </summary>
[Flags]
public enum CategoryFlags : uint
{
    None = 0,
    Build = 1 << 0,          // bin, obj, build, Debug, Release, x64, x86
    Cache = 1 << 1,          // cache, temp, tmp
    Log = 1 << 2,            // log, logs, *.log
    Git = 1 << 3,            // .git
    VisualStudio = 1 << 4,   // .vs, *.pdb, *.obj, *.ipch
    Package = 1 << 5,        // node_modules, NuGet, packages
    Container = 1 << 6,      // Docker, WSL
    System = 1 << 7,         // Windows, Program Files, ProgramData, WinSxS ... (16/61. 보호 경로)
    Media = 1 << 8,
    Archive = 1 << 9,
    Download = 1 << 10,      // Downloads (57. 오래된 다운로드)
    UserData = 1 << 11,      // Documents, Pictures, Videos, Desktop (62. 사용자 중요 폴더)
    SourceCode = 1 << 12,    // src, source 등 소스 트리 (62)
    Installer = 1 << 13,     // .msi, .exe 등 설치 파일 (57. 오래된 설치 파일)
    Temp = 1 << 14,          // temp, tmp (54. 임시 파일을 캐시와 구분)

    // ---- 보호 등급 1차 분류용 (경로 문자열 없이 대량 필터링) ----
    /// <summary>P0 후보: System32 / WinSxS / servicing / Boot / Recovery / System Volume Information.</summary>
    SystemCore = 1u << 15,
    /// <summary>P1 후보: 사용자 AppData.</summary>
    AppData = 1u << 16,
}
