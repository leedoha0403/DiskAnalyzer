using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskAnalyzer.Core.Services;

/// <summary>
/// 15. 파일 / 폴더 우클릭 메뉴 / 16. 삭제 안전성.
/// 삭제는 기본적으로 휴지통(FOF_ALLOWUNDO)을 사용한다. 영구 삭제는 호출자가 명시적으로 요청해야 한다.
/// </summary>
public static class ShellService
{
    public static void OpenInExplorer(string fullPath, bool isDirectory)
    {
        try
        {
            if (isDirectory && Directory.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{fullPath}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = true });
            }
        }
        catch { }
    }

    public static void ShowProperties(string fullPath)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_NOCLOSEPROCESS,
            lpVerb = "properties",
            lpFile = fullPath,
            nShow = 1,
        };
        ShellExecuteEx(ref info);
    }

    /// <summary>휴지통으로 이동. 성공하면 true.</summary>
    public static bool MoveToRecycleBin(string fullPath)
        => FileOperation(fullPath, FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING);

    /// <summary>
    /// 영구 삭제. UI 에서 별도 확인을 받은 경우에만 호출한다.
    /// 셸(SHFileOperation)이 아니라 <see cref="FastDeleter"/> 를 쓴다: 병렬 처리, 읽기 전용/사용 중 파일 재시도,
    /// 긴 경로 지원. 이미 없는 경로는 "지운 것과 같은 상태"이므로 성공으로 본다.
    /// </summary>
    public static bool DeletePermanently(string fullPath)
    {
        try
        {
            var r = FastDeleter.Delete(fullPath);
            return r.Success || r.NotFound;
        }
        catch
        {
            return false;
        }
    }

    private static bool FileOperation(string fullPath, ushort flags)
    {
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = fullPath + "\0\0",
                fFlags = flags,
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }
        catch
        {
            return false;
        }
    }

    // ---- interop ----

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);
}
