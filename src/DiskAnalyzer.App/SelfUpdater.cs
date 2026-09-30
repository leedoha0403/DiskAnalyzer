using System.Diagnostics;
using System.IO;

namespace DiskAnalyzer.App;

/// <summary>
/// 실행 중인 exe 를 내려받은 새 exe 로 바꾼다. 실행 중인 exe 는 스스로를 덮어쓸 수 없으므로,
/// 이 exe 의 임시 복사본을 --apply-update 로 띄운다: 그 복사본이 앱이 끝나기를 기다렸다가 파일을 바꾸고 다시 실행한다.
/// 교체에 실패하면 이전 exe 를 되돌리고(.bak) 그대로 다시 실행한다.
/// </summary>
public static class SelfUpdater
{
    public const string ApplyArgument = "--apply-update";
    public const string ExeAssetName = "DiskAnalyzer.exe";

    public static string UpdateDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskAnalyzer", "update");

    private static string LogPath => Path.Combine(UpdateDirectory, "update.log");

    /// <summary>실행 파일 폴더에 쓸 수 있고 단일 exe 로 실행 중이면 true(Program Files 등 쓰기 금지 위치면 false → zip 내려받기로 대체).</summary>
    public static bool CanReplaceInPlace()
    {
        var target = Environment.ProcessPath;
        if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        // dotnet.exe 로 띄운 개발 실행은 교체 대상이 아니다.
        if (Path.GetFileName(target).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var probe = target + ".write-test";
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>도우미를 띄운다. true 가 돌아오면 호출한 쪽은 곧바로 앱을 끝내야 한다.</summary>
    public static bool LaunchHelper(string newExePath)
    {
        try
        {
            var target = Environment.ProcessPath!;
            var helper = Path.Combine(Path.GetTempPath(), $"DiskAnalyzer-updater-{Guid.NewGuid():N}.exe");
            File.Copy(target, helper, overwrite: true);
            var psi = new ProcessStartInfo(helper) { UseShellExecute = false };
            psi.ArgumentList.Add(ApplyArgument);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add(target);
            psi.ArgumentList.Add(newExePath);
            return Process.Start(psi) is not null;
        }
        catch (Exception ex)
        {
            Log("helper launch failed: " + ex);
            return false;
        }
    }

    /// <summary>도우미 진입점(임시 복사본에서, UI 가 뜨기 전에 실행). args: --apply-update &lt;pid&gt; &lt;대상 exe&gt; &lt;새 exe&gt;</summary>
    public static void RunHelper(string[] args)
    {
        var target = args[2];
        var newExe = args[3];
        var backup = target + ".bak";
        Log($"replacing {target}");
        try
        {
            if (int.TryParse(args[1], out var pid))
            {
                try
                {
                    using var parent = Process.GetProcessById(pid);
                    if (!parent.WaitForExit(TimeSpan.FromSeconds(30)))
                    {
                        Log("app did not exit in time, aborting");
                        return;
                    }
                }
                catch (ArgumentException)
                {
                    // 이미 끝났다
                }
            }

            Retry(() =>
            {
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(target, backup);
            });

            try
            {
                Retry(() => File.Copy(newExe, target, overwrite: true));
            }
            catch
            {
                Retry(() =>
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(backup, target);
                });
                throw;
            }

            try { File.Delete(newExe); } catch { }
            Log("replaced, restarting");
        }
        catch (Exception ex)
        {
            Log("update failed (previous exe restored if possible): " + ex);
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("restart failed: " + ex);
        }
    }

    /// <summary>지난 업데이트가 남긴 .bak. 새 빌드가 뜬 뒤에는 지워도 된다.</summary>
    public static void CleanupBackup()
    {
        try
        {
            var target = Environment.ProcessPath;
            if (target is null) return;
            var backup = target + ".bak";
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch
        {
        }
    }

    // 프로세스가 끝난 직후에도 백신 / 핸들 정리 때문에 exe 가 잠시 잠겨 있을 수 있다.
    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(300);
            }
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(UpdateDirectory);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} updater: {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
