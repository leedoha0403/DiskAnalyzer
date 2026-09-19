using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DiskAnalyzer.Core.Interop;
using DiskAnalyzer.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace DiskAnalyzer.Core.Scanning.Ntfs;

/// <summary>MFT 를 통째로 읽어 만든 원본 레코드 테이블(SoA).</summary>
internal sealed class MftRecordTable
{
    public const byte FlagInUse = 1;
    public const byte FlagDirectory = 2;
    public const byte FlagHasName = 4;

    public required int Count { get; init; }
    public required int[] ParentIndex { get; init; }   // 부모 디렉터리의 MFT 인덱스
    public required long[] Size { get; init; }
    public required long[] Time { get; init; }
    public required long[] Created { get; init; }
    public required long[] Accessed { get; init; }
    public required uint[] Attributes { get; init; }
    public required long[] NameHandle { get; init; }
    public required byte[] Flags { get; init; }
    public required StringPool Pool { get; init; }

    public bool IsInUse(int i) => (Flags[i] & FlagInUse) != 0 && (Flags[i] & FlagHasName) != 0;
    public bool IsDirectory(int i) => (Flags[i] & FlagDirectory) != 0;
}

/// <summary>
/// 28. NTFS Fast Scan.
///
/// 디렉터리를 하나씩 열거하는 대신 볼륨의 $MFT(Master File Table)를 통째로 순차 읽어
/// 모든 파일의 이름/크기/시간/부모를 한 번에 얻는다.
/// 100만 파일 기준 FindFirstFile 방식이 수만 번의 랜덤 I/O 를 유발하는 반면
/// 이 방식은 수백 MB 의 순차 읽기 한 번으로 끝나므로 WizTree 급 속도가 나온다.
///
/// [제약]
///  - NTFS 전용
///  - 볼륨 핸들을 열어야 하므로 관리자 권한 필요
///  - 실패하면 예외 없이 false 를 돌려주고 호출자가 Compatibility 로 fallback 한다(28).
/// </summary>
internal sealed class MftReader : IDisposable
{
    private const int ReadChunkBytes = 4 * 1024 * 1024;

    private SafeFileHandle? _volume;
    private int _bytesPerSector;
    private int _bytesPerCluster;
    private int _fileRecordSize;
    private long _mftStartLcn;

    public string? Error { get; private set; }
    public long RecordsRead;
    public long RecordsTotal;

    public bool Open(char driveLetter)
    {
        try
        {
            _volume = Win32.CreateFile($"\\\\.\\{char.ToUpperInvariant(driveLetter)}:",
                Win32.GENERIC_READ, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
                IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);

            if (_volume.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                Error = err == Win32.ERROR_ACCESS_DENIED
                    ? "볼륨 직접 읽기 권한이 없습니다(관리자 권한 필요)."
                    : $"볼륨을 열 수 없습니다. (오류 {err})";
                return false;
            }

            Span<byte> boot = stackalloc byte[512];
            if (RandomAccess.Read(_volume, boot, 0) < 512)
            {
                Error = "부트 섹터를 읽지 못했습니다.";
                return false;
            }

            // NTFS 부트 섹터(BPB) 파싱
            if (!(boot[3] == (byte)'N' && boot[4] == (byte)'T' && boot[5] == (byte)'F' && boot[6] == (byte)'S'))
            {
                Error = "NTFS 볼륨이 아닙니다.";
                return false;
            }

            _bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[0x0B..]);
            int sectorsPerCluster = boot[0x0D];
            _bytesPerCluster = _bytesPerSector * sectorsPerCluster;
            _mftStartLcn = BinaryPrimitives.ReadInt64LittleEndian(boot[0x30..]);

            sbyte clustersPerRecord = (sbyte)boot[0x40];
            _fileRecordSize = clustersPerRecord >= 0
                ? clustersPerRecord * _bytesPerCluster
                : 1 << (-clustersPerRecord);

            if (_bytesPerSector <= 0 || _bytesPerCluster <= 0 || _fileRecordSize <= 0)
            {
                Error = "부트 섹터 값이 올바르지 않습니다.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
    }

    /// <summary>MFT 전체를 읽어 레코드 테이블을 만든다.</summary>
    public MftRecordTable? ReadAll(bool showHidden, bool showSystem, CancellationToken ct)
    {
        if (_volume == null) return null;

        var mftRuns = ReadMftRunList(out long mftDataSize);
        if (mftRuns == null || mftRuns.Count == 0) return null;

        int recordCount = (int)Math.Min(int.MaxValue - 16, mftDataSize / _fileRecordSize);
        RecordsTotal = recordCount;

        var parent = new int[recordCount];
        var size = new long[recordCount];
        var time = new long[recordCount];
        var created = new long[recordCount];
        var accessed = new long[recordCount];
        var attrs = new uint[recordCount];
        var nameHandle = new long[recordCount];
        var flags = new byte[recordCount];
        var pool = new StringPool();

        byte[] buffer = GC.AllocateUninitializedArray<byte>(ReadChunkBytes);
        Span<char> nameBuf = stackalloc char[256];

        long globalRecord = 0;
        foreach (var run in mftRuns)
        {
            long runBytes = run.ClusterCount * (long)_bytesPerCluster;
            long runOffset = run.Lcn * (long)_bytesPerCluster;

            for (long done = 0; done < runBytes && globalRecord < recordCount;)
            {
                if (ct.IsCancellationRequested) return null;

                int toRead = (int)Math.Min(ReadChunkBytes, runBytes - done);
                toRead -= toRead % _fileRecordSize;
                if (toRead <= 0) break;

                int read = RandomAccess.Read(_volume, buffer.AsSpan(0, toRead), runOffset + done);
                if (read < _fileRecordSize) break;
                done += read;

                int records = read / _fileRecordSize;
                for (int r = 0; r < records && globalRecord < recordCount; r++, globalRecord++)
                {
                    var rec = buffer.AsSpan(r * _fileRecordSize, _fileRecordSize);
                    ParseRecord(rec, (int)globalRecord, parent, size, time, created, accessed, attrs,
                        nameHandle, flags, pool, nameBuf, showHidden, showSystem);
                }
                Volatile.Write(ref RecordsRead, globalRecord);
            }
        }

        return new MftRecordTable
        {
            Count = recordCount,
            ParentIndex = parent,
            Size = size,
            Time = time,
            Created = created,
            Accessed = accessed,
            Attributes = attrs,
            NameHandle = nameHandle,
            Flags = flags,
            Pool = pool,
        };
    }

    // ------------------------------------------------------------------ 내부

    private readonly record struct DataRun(long Lcn, long ClusterCount);

    /// <summary>MFT 레코드 0번($MFT 자신)의 $DATA 런리스트 = MFT 가 디스크에 놓인 위치들.</summary>
    private List<DataRun>? ReadMftRunList(out long dataSize)
    {
        dataSize = 0;
        var record = new byte[_fileRecordSize];
        if (RandomAccess.Read(_volume!, record, _mftStartLcn * _bytesPerCluster) < _fileRecordSize)
        {
            Error = "$MFT 레코드를 읽지 못했습니다.";
            return null;
        }
        if (!ApplyFixups(record))
        {
            Error = "$MFT 레코드 서명이 올바르지 않습니다.";
            return null;
        }

        int attrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14));
        int pos = attrOffset;

        while (pos + 8 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos));
            if (type == 0xFFFFFFFF) break;
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos + 4));
            if (length <= 0 || pos + length > record.Length) break;

            if (type == 0x80 && record[pos + 8] != 0)   // 비상주 $DATA
            {
                dataSize = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(pos + 0x30));
                int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(pos + 0x20));
                return ParseDataRuns(record.AsSpan(pos + runOffset, length - runOffset));
            }
            pos += length;
        }

        Error = "$MFT 의 $DATA 속성을 찾지 못했습니다.";
        return null;
    }

    /// <summary>
    /// NTFS 데이터 런 디코딩.
    /// 각 런은 [헤더 1바이트][길이][LCN 델타] 형태이고, LCN 은 직전 런에 대한 "부호 있는 상대값"이다.
    /// </summary>
    private static List<DataRun> ParseDataRuns(ReadOnlySpan<byte> buf)
    {
        var runs = new List<DataRun>(16);
        int p = 0;
        long lcn = 0;

        while (p < buf.Length && buf[p] != 0)
        {
            int lenBytes = buf[p] & 0x0F;
            int offBytes = (buf[p] >> 4) & 0x0F;
            p++;
            if (lenBytes == 0 || p + lenBytes + offBytes > buf.Length) break;

            long count = 0;
            for (int i = 0; i < lenBytes; i++) count |= (long)buf[p + i] << (i * 8);
            p += lenBytes;

            if (offBytes == 0)
            {
                p += offBytes;      // sparse run - MFT 에서는 사실상 등장하지 않는다
                continue;
            }

            long delta = 0;
            for (int i = 0; i < offBytes; i++) delta |= (long)buf[p + i] << (i * 8);
            // 최상위 바이트가 음수면 부호 확장
            if ((buf[p + offBytes - 1] & 0x80) != 0) delta |= -1L << (offBytes * 8);
            p += offBytes;

            lcn += delta;
            runs.Add(new DataRun(lcn, count));
        }
        return runs;
    }

    /// <summary>
    /// Update Sequence Array 복원.
    /// NTFS 는 각 섹터의 마지막 2바이트를 검사용 값으로 바꿔 두고 원본을 USA 에 보관한다.
    /// 되돌리지 않으면 레코드 내용이 512바이트마다 2바이트씩 깨진 상태가 된다.
    /// </summary>
    private bool ApplyFixups(Span<byte> record)
    {
        if (record.Length < 0x30) return false;
        if (!(record[0] == (byte)'F' && record[1] == (byte)'I' && record[2] == (byte)'L' && record[3] == (byte)'E'))
            return false;

        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x04..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[0x06..]);
        if (usaOffset <= 0 || usaCount <= 1) return false;
        if (usaOffset + usaCount * 2 > record.Length) return false;

        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = i * _bytesPerSector - 2;
            if (sectorEnd + 2 > record.Length) break;
            record[sectorEnd] = record[usaOffset + i * 2];
            record[sectorEnd + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    private void ParseRecord(Span<byte> record, int index,
        int[] parent, long[] size, long[] time, long[] created, long[] accessed, uint[] attrs,
        long[] nameHandle, byte[] flags,
        StringPool pool, Span<char> nameBuf, bool showHidden, bool showSystem)
    {
        parent[index] = -1;

        if (!ApplyFixups(record)) return;

        ushort recFlags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
        bool inUse = (recFlags & 0x0001) != 0;
        bool isDir = (recFlags & 0x0002) != 0;
        if (!inUse) return;

        long baseRef = BinaryPrimitives.ReadInt64LittleEndian(record[0x20..]);
        int baseIndex = (int)(baseRef & 0x0000FFFFFFFFFFFF);
        bool isExtension = baseIndex != 0;

        int attrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        int pos = attrOffset;

        int bestNamespace = 99;
        int foundParent = -1;
        long foundName = -1;
        long dataSize = -1;
        long modified = 0;
        long createdTime = 0;
        long accessedTime = 0;
        uint fileAttributes = 0;

        while (pos + 16 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[pos..]);
            if (type == 0xFFFFFFFF) break;
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(pos + 4)..]);
            if (length <= 0 || pos + length > record.Length) break;

            bool nonResident = record[pos + 8] != 0;
            int attrNameLength = record[pos + 9];

            if (type == 0x10 && !nonResident)                   // $STANDARD_INFORMATION
            {
                int contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[(pos + 0x14)..]);
                int c = pos + contentOffset;
                if (c + 36 <= record.Length)
                {
                    // $STANDARD_INFORMATION: [0]생성 [8]수정 [16]MFT변경 [24]접근 [32]속성
                    createdTime = BinaryPrimitives.ReadInt64LittleEndian(record[c..]);
                    modified = BinaryPrimitives.ReadInt64LittleEndian(record[(c + 8)..]);
                    accessedTime = BinaryPrimitives.ReadInt64LittleEndian(record[(c + 24)..]);
                    fileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(record[(c + 32)..]);
                }
            }
            else if (type == 0x30 && !nonResident)               // $FILE_NAME
            {
                int contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[(pos + 0x14)..]);
                int c = pos + contentOffset;
                if (c + 0x42 <= record.Length)
                {
                    int nameLen = record[c + 0x40];
                    int nameSpace = record[c + 0x41];
                    // DOS 8.3 전용 이름(namespace 2)은 같은 파일의 Win32 이름이 따로 있으므로 우선순위를 낮춘다.
                    int priority = nameSpace == 2 ? 10 : nameSpace;
                    if (priority < bestNamespace && c + 0x42 + nameLen * 2 <= record.Length && nameLen > 0)
                    {
                        bestNamespace = priority;
                        long pref = BinaryPrimitives.ReadInt64LittleEndian(record[c..]);
                        foundParent = (int)(pref & 0x0000FFFFFFFFFFFF);

                        int n = Math.Min(nameLen, nameBuf.Length);
                        for (int i = 0; i < n; i++)
                            nameBuf[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(record[(c + 0x42 + i * 2)..]);
                        foundName = pool.Add(nameBuf[..n]);
                    }
                }
            }
            else if (type == 0x80 && attrNameLength == 0)        // 이름 없는 $DATA = 파일 본문
            {
                if (!nonResident)
                {
                    dataSize = BinaryPrimitives.ReadUInt32LittleEndian(record[(pos + 0x10)..]);
                }
                else
                {
                    long startVcn = BinaryPrimitives.ReadInt64LittleEndian(record[(pos + 0x10)..]);
                    // 조각난 파일은 $DATA 가 여러 레코드로 나뉘는데 실제 크기는 startVCN==0 조각에만 들어 있다.
                    if (startVcn == 0)
                        dataSize = BinaryPrimitives.ReadInt64LittleEndian(record[(pos + 0x30)..]);
                }
            }

            pos += length;
        }

        if (isExtension)
        {
            // 확장 레코드: 자체 노드를 만들지 않고 크기만 기본 레코드에 더해 준다.
            if (dataSize > 0 && (uint)baseIndex < (uint)size.Length) size[baseIndex] += dataSize;
            return;
        }

        if (foundName < 0 || foundParent < 0) return;

        if (!showHidden && (fileAttributes & Win32.FILE_ATTRIBUTE_HIDDEN) != 0) return;
        if (!showSystem && (fileAttributes & Win32.FILE_ATTRIBUTE_SYSTEM) != 0) return;

        parent[index] = foundParent;
        nameHandle[index] = foundName;
        time[index] = modified;
        created[index] = createdTime;
        accessed[index] = accessedTime;
        attrs[index] = fileAttributes;
        if (!isDir && dataSize > 0) size[index] += dataSize;

        byte f = MftRecordTable.FlagInUse | MftRecordTable.FlagHasName;
        if (isDir) f |= MftRecordTable.FlagDirectory;
        flags[index] = f;
    }

    public void Dispose() => _volume?.Dispose();
}
