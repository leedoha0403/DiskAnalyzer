using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskAnalyzer.Core.Services;

/// <param name="ParentPid">트리 보기용 부모. 부모가 없거나 죽었거나 explorer/services 같은 시스템 프로세스면 0.</param>
/// <param name="RootApp">트리를 끝까지 거슬러 올라간 최상위 조상 이름(자신이 최상위면 빈 문자열). 예: pwsh 의 RootApp 은 claude.</param>
/// <param name="Ancestors">모든 조상 이름(공백 구분) - 검색용.</param>
/// <param name="Windowless">메인 창이 없다. 이런 프로세스는 Responding 이 항상 true 라 "응답 없음"으로는 못 거른다.</param>
/// <param name="IdleSeconds">CPU·I/O 변화가 없이 지나간 시간(우리가 관찰한 구간 기준).</param>
/// <param name="Suspicious">창 없이 오래 멈춰 있고 다른 앱의 자식이거나 부모가 죽은 프로세스 - 멈춤 의심.</param>
/// <param name="Orphaned">부모 프로세스가 이미 없다.</param>
public sealed record ProcessInfo(
    int Pid,
    string Name,
    string WindowTitle,
    bool Responding,
    long MemoryBytes,
    DateTime? StartTime,
    double CpuPercent,
    double DiskBytesPerSecond,
    int ParentPid = 0,
    string RootApp = "",
    string Ancestors = "",
    bool Windowless = false,
    double IdleSeconds = 0,
    bool Suspicious = false,
    bool Orphaned = false)
{
    /// <summary>기본 화면에 올릴 만한가: 응답 없음이거나 멈춤 의심.</summary>
    public bool NeedsAttention => !Responding || Suspicious;
}

public enum KillOutcome { Success, AccessDenied, NotFound, Error }

/// <summary>
/// 게임 등이 죽다 만 채로 남는 좀비/응답 없음 프로세스를 정리한다.
/// 앱은 asInvoker 로 뜨므로 다른 사용자 소유이거나 보호된 프로세스는 TryKill 이 AccessDenied 를 돌려준다 —
/// 그럴 때만 <see cref="TryKillElevated"/> 로 같은 exe 를 관리자 권한으로 다시 띄워 그 프로세스 하나만 죽이고 빠지게 한다
/// (App.xaml.cs 의 --kill-pid 처리와 짝을 이룬다). 항상 관리자 권한을 요구하지 않는 이유는 app.manifest 참고.
/// </summary>
public static class ProcessCleanerService
{
    /// <summary>창 없는 프로세스가 이 시간 넘게 CPU·I/O 변화가 없으면 멈춤 의심으로 본다.</summary>
    public const double SuspectIdleSeconds = 300;

    private sealed record Sample(TimeSpan Cpu, long Io, DateTime At, DateTime? Start, DateTime IdleSince);

    private sealed class Raw
    {
        public int Pid;
        public string Name = "";
        public string WindowTitle = "";
        public bool Responding;
        public bool Windowless;
        public long Memory;
        public DateTime? Start;
        public double CpuPercent, DiskRate;
        public double IdleSeconds;
        public bool CountersReadable;
    }

    /// <summary>
    /// CPU%·디스크 속도는 누적값(총 CPU 시간, 총 입출력 바이트)의 두 시점 사이 차이로만 계산할 수 있다.
    /// 그래서 직전 호출의 누적값을 여기에 기억해 뒀다가 다음 호출에서 델타를 낸다 - ViewModel 이
    /// 창을 열어 둔 동안 주기적으로 ListProcesses 를 다시 부르는 것을 전제로 한다(작업 관리자와 같은 방식).
    /// 창이 닫혀 한동안 안 불리다 다시 열리면 그 첫 호출은 기준점이 없어 0%로 보인다.
    /// 멈춤 판정용 IdleSince 도 같이 들고 있어서, 창을 닫았다 다시 열어도 그 사이 변화가 없었다면 이어서 센다.
    /// </summary>
    private static readonly Dictionary<int, Sample> PrevSamples = new();
    private static readonly object SampleLock = new();

    /// <summary>
    /// 모든 프로세스를 돌려준다. 필터(응답 없음/멈춤 의심만 등)는 호출자 몫이다 -
    /// 검색은 필터와 무관하게 전체를 대상으로 해야 하기 때문이다.
    /// </summary>
    public static IReadOnlyList<ProcessInfo> ListProcesses()
    {
        int selfPid = Environment.ProcessId;
        var now = DateTime.UtcNow;
        var raws = new List<Raw>();
        var current = new Dictionary<int, Sample>();

        lock (SampleLock)
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == selfPid) continue;

                    var raw = new Raw
                    {
                        Pid = p.Id,
                        Name = p.ProcessName,
                        WindowTitle = SafeWindowTitle(p),
                        Windowless = SafeWindowless(p),
                        Responding = SafeResponding(p),
                        Memory = SafeMemory(p),
                        Start = SafeStartTime(p),
                    };

                    bool cpuOk = TryCpuTime(p, out var cpuTime);
                    bool ioOk = TryIoBytes(p, out long io);
                    raw.CountersReadable = cpuOk && ioOk;

                    DateTime idleSince = now;
                    if (PrevSamples.TryGetValue(raw.Pid, out var prev) && prev.Start == raw.Start)
                    {
                        double elapsed = (now - prev.At).TotalSeconds;
                        if (elapsed > 0.05)
                        {
                            raw.CpuPercent = Math.Max(0, (cpuTime - prev.Cpu).TotalMilliseconds
                                / (elapsed * 1000.0 * Environment.ProcessorCount) * 100.0);
                            raw.DiskRate = Math.Max(0, (io - prev.Io) / elapsed);
                        }

                        bool idle = (cpuTime - prev.Cpu).TotalMilliseconds < 5 && io - prev.Io <= 0;
                        idleSince = idle ? prev.IdleSince : now;
                    }

                    current[raw.Pid] = new Sample(cpuTime, io, now, raw.Start, idleSince);
                    raw.IdleSeconds = (now - idleSince).TotalSeconds;
                    raws.Add(raw);
                }
            }

            PrevSamples.Clear();
            foreach (var kv in current) PrevSamples[kv.Key] = kv.Value;
        }

        return BuildInfos(raws);
    }

    /// <summary>부모·조상·멈춤 의심 여부를 채워 ProcessInfo 로 만든다.</summary>
    private static List<ProcessInfo> BuildInfos(List<Raw> raws)
    {
        var parentOf = GetParentMap();
        var byPid = raws.ToDictionary(r => r.Pid);

        var treeParent = new Dictionary<int, int>();
        var orphaned = new HashSet<int>();

        foreach (var r in raws)
        {
            if (!parentOf.TryGetValue(r.Pid, out int ppid) || ppid == 0) continue;

            // 부모 PID 로 찾은 프로세스가 사실 PID 재사용으로 자식보다 나중에 뜬 다른 프로세스일 수 있다 - 그러면 부모는 이미 죽은 것이다.
            if (!byPid.TryGetValue(ppid, out var parent) || StartedAfter(parent.Start, r.Start))
            {
                orphaned.Add(r.Pid);
                continue;
            }

            // explorer·services 같은 시스템 프로세스 밑으로는 묶지 않는다 - 안 그러면 사용자가 띄운 모든 게 explorer 아래로 들어간다.
            if (IsTreeRoot(parent.Name)) continue;
            treeParent[r.Pid] = ppid;
        }

        var result = new List<ProcessInfo>(raws.Count);
        foreach (var r in raws)
        {
            var ancestors = new List<string>();
            int cur = r.Pid;
            string root = "";
            for (int guard = 0; guard < 32 && treeParent.TryGetValue(cur, out int up); guard++)
            {
                root = byPid[up].Name;
                ancestors.Add(root);
                cur = up;
            }

            bool isOrphan = orphaned.Contains(r.Pid);
            bool suspicious = r.Windowless
                              && r.CountersReadable
                              && r.IdleSeconds >= SuspectIdleSeconds
                              && !IsProtected(r.Name)
                              && (ancestors.Count > 0 || isOrphan);

            result.Add(new ProcessInfo(
                r.Pid, r.Name, r.WindowTitle, r.Responding, r.Memory, r.Start,
                r.CpuPercent, r.DiskRate,
                treeParent.TryGetValue(r.Pid, out int tp) ? tp : 0,
                root,
                string.Join(' ', ancestors),
                r.Windowless, r.IdleSeconds, suspicious, isOrphan));
        }

        return result
            .OrderBy(r => r.NeedsAttention ? 0 : 1)
            .ThenByDescending(r => r.MemoryBytes)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool StartedAfter(DateTime? parentStart, DateTime? childStart)
        => parentStart is { } p && childStart is { } c && p > c.AddSeconds(2);

    private static bool IsTreeRoot(string name)
        => IsProtected(name) || name.Equals("userinit", StringComparison.OrdinalIgnoreCase);

    private static bool SafeResponding(Process p) { try { return p.Responding; } catch { return true; } }
    private static string SafeWindowTitle(Process p) { try { return p.MainWindowTitle; } catch { return ""; } }
    private static bool SafeWindowless(Process p) { try { return p.MainWindowHandle == IntPtr.Zero; } catch { return true; } }
    private static long SafeMemory(Process p) { try { return p.WorkingSet64; } catch { return 0; } }
    private static DateTime? SafeStartTime(Process p) { try { return p.StartTime; } catch { return null; } }

    private static bool TryCpuTime(Process p, out TimeSpan cpu)
    {
        try { cpu = p.TotalProcessorTime; return true; }
        catch { cpu = TimeSpan.Zero; return false; }
    }

    private static bool TryIoBytes(Process p, out long bytes)
    {
        try
        {
            if (GetProcessIoCounters(p.Handle, out var io))
            {
                bytes = (long)(io.ReadTransferCount + io.WriteTransferCount);
                return true;
            }
        }
        catch
        {
            // 다른 사용자 소유·보호된 프로세스는 핸들을 못 얻는다 - 속도 0, 멈춤 판정 제외
        }

        bytes = 0;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

    // ---------------------------------------------------------------- 부모 PID (Toolhelp 스냅샷)

    private const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>pid → 부모 pid. 스냅샷을 못 얻으면 빈 사전(트리 없이 평평하게 보일 뿐 나머지 기능은 그대로 동작한다).</summary>
    private static Dictionary<int, int> GetParentMap()
    {
        var map = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool ok = Process32FirstW(snap, ref entry); ok; ok = Process32NextW(snap, ref entry))
                map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
        }
        finally
        {
            CloseHandle(snap);
        }

        return map;
    }

    /// <summary>
    /// 종료하면 세션·로그인 자체가 끊기는 핵심 시스템 프로세스. 이름만으로 거르므로 완벽하지는 않지만
    /// 실수로 explorer 나 csrss 를 눌러 세션을 날리는 사고는 막는다. UI 는 이 목록의 프로세스는 [종료] 버튼을 비활성화한다.
    /// </summary>
    public static readonly IReadOnlySet<string> ProtectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "explorer", "fontdrvhost", "sihost", "taskhostw", "ctfmon",
    };

    public static bool IsProtected(string processName) => ProtectedNames.Contains(processName);

    /// <summary>종료 요청 뒤 실제로 사라질 때까지 기다려 주는 시간. 파이프 I/O 에 걸린 프로세스는 몇 초 더 남기도 한다.</summary>
    private const int ExitWaitMs = 10_000;

    /// <summary>
    /// 지금 권한으로 바로 종료를 시도한다. 자식까지 함께 끝낸다.
    /// Process.Kill(entireProcessTree) 는 자식 하나만 실패해도 AggregateException 을 던져서 "권한 부족"과
    /// 일반 실패가 뒤섞였다 - 그래서 자손을 직접 모아 깊은 것부터 하나씩 끝내고 실패 원인을 구분한다.
    /// 자손 중 하나라도 접근 거부면 뿌리는 건드리지 않고 AccessDenied 를 돌려준다 - 관리자 권한 재시도가
    /// 같은 pid 로 다시 트리 전체를 정리할 수 있어야 하기 때문이다(뿌리만 먼저 죽으면 재시도할 pid 가 사라진다).
    /// </summary>
    public static KillOutcome TryKill(int pid)
    {
        Process root;
        try { root = Process.GetProcessById(pid); }
        catch (ArgumentException) { return KillOutcome.NotFound; }
        catch (InvalidOperationException) { return KillOutcome.NotFound; }
        catch (Win32Exception) { return KillOutcome.AccessDenied; }
        catch { return KillOutcome.Error; }

        using (root)
        {
            bool denied = false;
            bool failed = false;

            foreach (int childPid in DescendantsDeepestFirst(root))
            {
                switch (KillOne(childPid))
                {
                    case KillOutcome.AccessDenied: denied = true; break;
                    case KillOutcome.Error: failed = true; break;
                }
            }

            if (denied) return KillOutcome.AccessDenied;

            try
            {
                root.Kill();
            }
            catch (InvalidOperationException) { return KillOutcome.NotFound; }   // 이미 종료됨
            catch (Win32Exception) { if (!ExitedSafe(root)) return KillOutcome.AccessDenied; }
            catch { return KillOutcome.Error; }

            bool exited = false;
            try { exited = root.WaitForExit(ExitWaitMs); } catch { exited = ExitedSafe(root); }

            return exited && !failed ? KillOutcome.Success : KillOutcome.Error;
        }
    }

    private static bool ExitedSafe(Process p)
    {
        try { return p.HasExited; } catch { return false; }
    }

    private static KillOutcome KillOne(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
            return KillOutcome.Success;
        }
        catch (ArgumentException) { return KillOutcome.NotFound; }
        catch (InvalidOperationException) { return KillOutcome.NotFound; }
        catch (Win32Exception) { return KillOutcome.AccessDenied; }
        catch { return KillOutcome.Error; }
    }

    /// <summary>root 의 모든 자손 pid - 손자가 먼저 나오도록 깊은 순서. PID 재사용으로 잘못 엮인 프로세스는 시작 시각으로 걸러낸다.</summary>
    private static List<int> DescendantsDeepestFirst(Process root)
    {
        var parentOf = GetParentMap();
        var children = parentOf.GroupBy(kv => kv.Value, kv => kv.Key).ToDictionary(g => g.Key, g => g.ToList());

        var ordered = new List<int>();
        var seen = new HashSet<int> { root.Id };
        var queue = new Queue<(int Pid, DateTime? Start)>();
        queue.Enqueue((root.Id, SafeStartTime(root)));

        while (queue.Count > 0)
        {
            var (pid, start) = queue.Dequeue();
            if (!children.TryGetValue(pid, out var kids)) continue;

            foreach (int kid in kids)
            {
                if (!seen.Add(kid)) continue;

                DateTime? kidStart = null;
                try { using var kp = Process.GetProcessById(kid); kidStart = kp.StartTime; } catch { }
                if (StartedAfter(start, kidStart)) continue;   // 부모보다 먼저 뜬 "자식" = 부모 pid 를 재사용한 다른 계보

                ordered.Add(kid);
                queue.Enqueue((kid, kidStart));
            }
        }

        ordered.Reverse();   // 너비 우선으로 모았으니 뒤집으면 깊은 것이 먼저
        return ordered;
    }

    /// <summary>
    /// 같은 실행 파일을 --kill-pid=&lt;pid[,pid...]&gt; 인자로 UAC 상승 재실행해서 그 프로세스(들)만 죽이고
    /// 즉시 종료하게 한다(App.xaml.cs 참고). 반환값은 실패한 개수 - 0 이면 전부 성공, 음수면 상승 자체가
    /// 안 됐거나(exe 경로 확인 불가) 사용자가 UAC 를 취소한 것이다.
    /// </summary>
    public static int TryKillElevated(IReadOnlyList<int> pids)
    {
        try
        {
            // MainModule.FileName 이 아니라 Environment.ProcessPath 를 써야 한다 - 자체 포함(단일 파일) 배포에서는
            // MainModule.FileName 이 실행 중 풀린 내부 임시 경로를 가리킬 수 있고, 그걸로 재실행하면
            // ".NET 이 없습니다" 오류가 뜬다(MainWindow.xaml.cs 의 관리자 권한 재실행과 같은 방식으로 맞춘다).
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return -1;

            using var proc = Process.Start(new ProcessStartInfo(exe, $"--kill-pid={string.Join(",", pids)}")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (proc == null) return -1;

            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch
        {
            return -1; // UAC 취소 포함
        }
    }

    public static bool TryKillElevated(int pid) => TryKillElevated(new[] { pid }) == 0;
}
