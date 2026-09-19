using System.Runtime.InteropServices;
using System.Security.Principal;
using DiskAnalyzer.Core.Interop;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Services;

/// <summary>4.1 드라이브 요약 영역 + 22. 스레드 수 결정을 위한 매체 종류 판별.</summary>
public static class DriveService
{
    private static readonly Dictionary<char, DriveKind> KindCache = new();

    public static IReadOnlyList<DriveInfoRow> GetLocalDrives()
    {
        var list = new List<DriveInfoRow>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed) continue;

            bool ready = d.IsReady;
            long total = 0, free = 0;
            string fs = string.Empty, label = string.Empty;

            if (ready)
            {
                try
                {
                    total = d.TotalSize;
                    free = d.TotalFreeSpace;
                    fs = d.DriveFormat;
                    label = d.VolumeLabel;
                }
                catch (IOException) { ready = false; }
                catch (UnauthorizedAccessException) { ready = false; }
            }

            list.Add(new DriveInfoRow
            {
                Name = d.Name.TrimEnd('\\'),
                RootPath = d.Name,
                Label = label,
                FileSystem = fs,
                TotalSize = total,
                FreeSpace = free,
                IsReady = ready,
                Kind = ready ? DetectKind(d.Name[0]) : DriveKind.Unknown,
            });
        }
        return list;
    }

    public static DriveInfoRow? GetDrive(string rootPath)
    {
        string name = rootPath.Length >= 2 ? rootPath[..2] : rootPath;
        return GetLocalDrives().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// IOCTL_STORAGE_QUERY_PROPERTY(StorageDeviceSeekPenaltyProperty) 로 SSD/HDD 를 구분한다.
    /// dwDesiredAccess=0 으로 열기 때문에 관리자 권한이 필요 없다.
    /// </summary>
    public static DriveKind DetectKind(char letter)
    {
        letter = char.ToUpperInvariant(letter);
        lock (KindCache)
        {
            if (KindCache.TryGetValue(letter, out var cached)) return cached;
        }

        var kind = DriveKind.Unknown;
        try
        {
            using var h = Win32.CreateFile($"\\\\.\\{letter}:", 0,
                Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);

            if (!h.IsInvalid)
            {
                var query = new Win32.STORAGE_PROPERTY_QUERY { PropertyId = 7, QueryType = 0 };
                int inSize = Marshal.SizeOf<Win32.STORAGE_PROPERTY_QUERY>();
                int outSize = Marshal.SizeOf<Win32.DEVICE_SEEK_PENALTY_DESCRIPTOR>();
                IntPtr inBuf = Marshal.AllocHGlobal(inSize);
                IntPtr outBuf = Marshal.AllocHGlobal(outSize);
                try
                {
                    Marshal.StructureToPtr(query, inBuf, false);
                    if (Win32.DeviceIoControl(h, Win32.IOCTL_STORAGE_QUERY_PROPERTY,
                            inBuf, (uint)inSize, outBuf, (uint)outSize, out _, IntPtr.Zero))
                    {
                        var desc = Marshal.PtrToStructure<Win32.DEVICE_SEEK_PENALTY_DESCRIPTOR>(outBuf);
                        kind = desc.IncursSeekPenalty ? DriveKind.Hdd : DriveKind.Ssd;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuf);
                    Marshal.FreeHGlobal(outBuf);
                }
            }
        }
        catch
        {
            kind = DriveKind.Unknown;
        }

        lock (KindCache) KindCache[letter] = kind;
        return kind;
    }

    /// <summary>28. NTFS Fast Scan 가능 여부 판단에 필요한 관리자 권한 확인.</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static bool IsNtfs(string rootPath)
    {
        try
        {
            var d = new DriveInfo(rootPath[..1]);
            return d.IsReady && string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static long GetVolumeUsedBytes(string rootPath)
    {
        try
        {
            var d = new DriveInfo(rootPath[..1]);
            return d.IsReady ? d.TotalSize - d.TotalFreeSpace : 0;
        }
        catch { return 0; }
    }
}
