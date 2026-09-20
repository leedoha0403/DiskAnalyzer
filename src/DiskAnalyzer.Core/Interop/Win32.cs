using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskAnalyzer.Core.Interop;

/// <summary>
/// 27. Windows API 최적화.
/// FindFirstFileEx + FindExInfoBasic + FIND_FIRST_EX_LARGE_FETCH 조합을 사용한다.
///  - FindExInfoBasic : 8.3 단축 이름(cAlternateFileName)을 채우지 않는다. NTFS 에서 이 필드는
///                      별도 조회를 유발하므로 끄는 것만으로 열거가 눈에 띄게 빨라진다.
///  - LARGE_FETCH     : 커널이 한 번의 요청으로 더 많은 디렉터리 엔트리를 가져오게 해
///                      FindNextFile 당 발생하는 syscall 횟수를 줄인다.
/// 열거 결과(WIN32_FIND_DATAW)에 크기/시간/속성이 모두 들어 있으므로
/// 파일마다 GetFileAttributesEx 같은 추가 호출을 하지 않는다(26/27).
/// </summary>
internal static class Win32
{
    internal const int MAX_PATH = 260;
    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    internal const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
    internal const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;
    internal const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_PATH_NOT_FOUND = 3;
    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_NO_MORE_FILES = 18;

    internal const int FindExInfoBasic = 1;
    internal const int FindExSearchNameMatch = 0;
    internal const int FIND_FIRST_EX_LARGE_FETCH = 2;

    /// <summary>
    /// Pack = 4 필수.
    /// 네이티브 WIN32_FIND_DATAW 는 DWORD/FILETIME(=DWORD 2개) 만으로 구성되어 4바이트 정렬이다.
    /// 기본 Pack(8)으로 두면 long 필드가 8바이트 경계로 밀려 레이아웃이 깨진다.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    internal unsafe struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        public fixed char cFileName[MAX_PATH];
        public fixed char cAlternateFileName[14];

        public long Size => ((long)nFileSizeHigh << 32) | nFileSizeLow;
        public bool IsDirectory => (dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        public bool IsReparsePoint => (dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindFirstFileExW")]
    internal static extern SafeFindHandle FindFirstFileEx(
        string lpFileName,
        int fInfoLevelId,
        out WIN32_FIND_DATAW lpFindFileData,
        int fSearchOp,
        IntPtr lpSearchFilter,
        int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindNextFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindNextFile(SafeFindHandle hFindFile, out WIN32_FIND_DATAW lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindClose(IntPtr hFindFile);

    /// <summary>
    /// 1/2/12. 새로고침 / 삭제 전 최종 검증용.
    /// 파일을 열지 않고 메타데이터만 가져오므로 사용 중인 파일에도 안전하다.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFileAttributesExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileAttributesEx(string lpFileName, int fInfoLevelId,
        out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    internal static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadFile(SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove,
        out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;      // 7 = StorageDeviceSeekPenaltyProperty
        public int QueryType;       // 0 = PropertyStandardQuery
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    // ------------------------------------------------------------------ 고속 삭제(FastDeleter)

    internal const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    internal const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;   // Junction
    internal const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;

    internal const int ERROR_SHARING_VIOLATION = 32;
    internal const int ERROR_LOCK_VIOLATION = 33;
    internal const int ERROR_DIR_NOT_EMPTY = 145;

    internal const uint DELETE = 0x00010000;
    internal const uint FILE_READ_ATTRIBUTES = 0x00000080;
    internal const uint FILE_SHARE_DELETE = 0x00000004;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    internal const int FileDispositionInfo = 4;
    internal const int FileDispositionInfoEx = 21;

    internal const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;
    internal const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2;
    internal const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_DISPOSITION_INFO_EX { public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_DISPOSITION_INFO { public byte DeleteFile; }   // Win32 BOOLEAN = 1 byte

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "DeleteFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteFile(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RemoveDirectoryW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveDirectory(string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetFileAttributesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFileAttributes(string lpFileName, uint dwFileAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFileAttributesW")]
    internal static extern uint GetFileAttributes(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass,
        ref FILE_DISPOSITION_INFO_EX lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass,
        ref FILE_DISPOSITION_INFO lpFileInformation, uint dwBufferSize);

    /// <summary>스캔 중 이동식 미디어 없음 등의 시스템 오류 대화상자를 억제한다.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetThreadErrorMode(uint dwNewMode, out uint lpOldMode);

    internal const uint SEM_FAILCRITICALERRORS = 0x0001;

    // ---- 빠른 이동: 같은 볼륨 이름 바꾸기 / 다른 볼륨 복사 / 여유 공간 ----

    internal const uint MOVEFILE_REPLACE_EXISTING = 0x1;
    internal const uint COPY_FILE_FAIL_IF_EXISTS = 0x1;
    internal const int ERROR_NOT_SAME_DEVICE = 17;
    internal const int ERROR_REQUEST_ABORTED = 1235;
    internal const int ERROR_DISK_FULL = 112;
    internal const int ERROR_HANDLE_DISK_FULL = 39;

    internal const uint PROGRESS_CONTINUE = 0;
    internal const uint PROGRESS_CANCEL = 1;

    /// <summary>MOVEFILE_COPY_ALLOWED 를 주지 않으므로 다른 볼륨이면 ERROR_NOT_SAME_DEVICE 로 실패한다. 이 실패가 "볼륨이 다르다"는 가장 정확한 신호다.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    internal delegate uint CopyProgressRoutine(
        long totalFileSize, long totalBytesTransferred,
        long streamSize, long streamBytesTransferred,
        uint streamNumber, uint callbackReason,
        IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CopyFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CopyFileEx(
        string lpExistingFileName, string lpNewFileName,
        CopyProgressRoutine? lpProgressRoutine, IntPtr lpData,
        ref int pbCancel, uint dwCopyFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);
}

internal sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFindHandle() : base(true) { }
    protected override bool ReleaseHandle() => Win32.FindClose(handle);
}
