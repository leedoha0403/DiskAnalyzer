using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskAnalyzer.Core.Services;

/// <summary>파일(들)을 붙들고 있는 프로세스 한 개.</summary>
public sealed record LockingProcess(int Pid, string Name, string AppName, bool IsService);

/// <summary>
/// Windows Restart Manager 로 "이 파일을 지금 누가 잡고 있나"를 묻는다.
/// 파일을 열거나 잠그지 않고 조회만 하므로 사용 중인 파일에도 안전하다.
/// 폴더는 Restart Manager 가 직접 다루지 못해서 안의 파일 일부(<see cref="MaxFilesPerQuery"/> 개까지)를 대신 묻는다.
/// </summary>
public static class FileLockFinder
{
    public const int MaxFilesPerQuery = 300;

    private const int ERROR_SUCCESS = 0;
    private const int ERROR_MORE_DATA = 234;
    private const uint RmServiceApp = 3;   // RM_APP_TYPE.RmService

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public uint ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint handle, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint handle, uint nFiles, string[] files,
        uint nApplications, RM_UNIQUE_PROCESS[]? applications, uint nServices, string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint handle, out uint needed, ref uint count,
        [In, Out] RM_PROCESS_INFO[]? info, out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint handle);

    /// <summary>주어진 파일/폴더 경로들을 잡고 있는 프로세스. 못 알아내면(권한 등) 빈 목록.</summary>
    public static IReadOnlyList<LockingProcess> Find(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (files.Count >= MaxFilesPerQuery) break;
            try
            {
                if (File.Exists(path)) files.Add(path);
                else if (Directory.Exists(path))
                    foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    }))
                    {
                        files.Add(f);
                        if (files.Count >= MaxFilesPerQuery) break;
                    }
            }
            catch (Exception)
            {
                // 이 경로는 건너뛴다
            }
        }

        return files.Count == 0 ? Array.Empty<LockingProcess>() : Query(files.ToArray());
    }

    private static IReadOnlyList<LockingProcess> Query(string[] files)
    {
        var key = new StringBuilder(33);   // CCH_RM_SESSION_KEY + 1
        if (RmStartSession(out uint session, 0, key) != ERROR_SUCCESS) return Array.Empty<LockingProcess>();

        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != ERROR_SUCCESS)
                return Array.Empty<LockingProcess>();

            uint needed = 0, count = 0;
            RM_PROCESS_INFO[]? info = null;
            int rc = ERROR_MORE_DATA;
            for (int attempt = 0; attempt < 4 && rc == ERROR_MORE_DATA; attempt++)
            {
                rc = RmGetList(session, out needed, ref count, info, out _);
                if (rc == ERROR_MORE_DATA)
                {
                    count = needed + 4;   // 조회 사이에 늘어날 수 있어 여유를 둔다
                    info = new RM_PROCESS_INFO[count];
                }
            }

            if (rc != ERROR_SUCCESS || info == null) return Array.Empty<LockingProcess>();

            var result = new List<LockingProcess>();
            foreach (var p in info.Take((int)count))
            {
                int pid = p.Process.dwProcessId;
                if (pid <= 4) continue;   // System / Idle

                string name;
                try { using var proc = Process.GetProcessById(pid); name = proc.ProcessName; }
                catch { continue; }       // 조회 사이에 이미 끝남

                result.Add(new LockingProcess(pid, name, p.strAppName ?? name, p.ApplicationType == RmServiceApp));
            }

            return result.DistinctBy(r => r.Pid).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<LockingProcess>();
        }
        finally
        {
            RmEndSession(session);
        }
    }
}
