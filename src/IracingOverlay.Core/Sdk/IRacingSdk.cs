using System.IO.MemoryMappedFiles;
using System.Text;

namespace IracingOverlay.Core.Sdk;

public enum VarType { Char = 0, Bool = 1, Int = 2, BitField = 3, Float = 4, Double = 5 }

public sealed record VarHeader(string Name, VarType Type, int Offset, int Count, string Unit, string Description)
{
    public int ElementSize => Type switch
    {
        VarType.Char or VarType.Bool => 1,
        VarType.Double => 8,
        _ => 4,
    };
}

/// <summary>
/// Minimal reader for iRacing's shared-memory telemetry interface (Local\IRSDKMemMapFileName).
///
/// Layout (irsdk_defines.h):
///   irsdk_header   at offset 0   (48 bytes + 4 x irsdk_varBuf of 16 bytes)
///   var headers    at header.varHeaderOffset (144 bytes each)
///   session info   at header.sessionInfoOffset (YAML, Latin-1)
///   telemetry      rotating buffers at varBuf[i].bufOffset, each header.bufLen bytes
/// </summary>
public sealed class IRacingSdk : IDisposable
{
    private const string MemMapName = @"Local\IRSDKMemMapFileName";
    private const int StatusConnected = 1;

    private const int HdrStatus = 4;
    private const int HdrSessionInfoUpdate = 12;
    private const int HdrSessionInfoLen = 16;
    private const int HdrSessionInfoOffset = 20;
    private const int HdrNumVars = 24;
    private const int HdrVarHeaderOffset = 28;
    private const int HdrNumBuf = 32;
    private const int HdrBufLen = 36;
    private const int HdrVarBuf = 48;
    private const int VarBufSize = 16;
    private const int VarHeaderSize = 144;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private Dictionary<string, VarHeader> _vars = new(StringComparer.Ordinal);
    private int _lastSessionInfoUpdate = -1;

    public bool IsOpen => _view != null;

    /// <summary>True when the sim is running and has a live session (status bit set).</summary>
    public bool IsConnected => _view != null && (_view.ReadInt32(HdrStatus) & StatusConnected) != 0;

    public IReadOnlyDictionary<string, VarHeader> Vars => _vars;

    public bool TryOpen()
    {
        if (_view != null) return true;
        try
        {
            _mmf = MemoryMappedFile.OpenExisting(MemMapName, MemoryMappedFileRights.Read);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _lastSessionInfoUpdate = -1;
            _vars.Clear();
            return true;
        }
        catch (FileNotFoundException)
        {
            Close();
            return false;
        }
    }

    public void Close()
    {
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
        _vars = new(StringComparer.Ordinal);
        _lastSessionInfoUpdate = -1;
    }

    public void Dispose() => Close();

    /// <summary>Copies the most recent telemetry buffer. Returns null if not connected.</summary>
    public TelemetryFrame? ReadTelemetry()
    {
        var view = _view;
        if (view == null || !IsConnected) return null;

        int numVars = view.ReadInt32(HdrNumVars);
        if (_vars.Count != numVars) LoadVarHeaders(view, numVars);

        int numBuf = view.ReadInt32(HdrNumBuf);
        int bufLen = view.ReadInt32(HdrBufLen);
        if (numBuf <= 0 || bufLen <= 0) return null;

        var data = new byte[bufLen];

        // iRacing writes into rotating buffers; take the newest and verify it wasn't
        // overwritten while we copied it (tick count unchanged).
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int latest = 0, latestTick = int.MinValue;
            for (int i = 0; i < numBuf && i < 4; i++)
            {
                int tick = view.ReadInt32(HdrVarBuf + i * VarBufSize);
                if (tick > latestTick) { latestTick = tick; latest = i; }
            }

            int bufBase = HdrVarBuf + latest * VarBufSize;
            int offset = view.ReadInt32(bufBase + 4);
            view.ReadArray(offset, data, 0, bufLen);

            if (view.ReadInt32(bufBase) == latestTick)
                return new TelemetryFrame(data, _vars, latestTick);
        }
        return null;
    }

    /// <summary>Returns the session-info YAML if it changed since the last call, otherwise null.</summary>
    public string? ReadSessionInfoIfChanged()
    {
        var view = _view;
        if (view == null) return null;

        int update = view.ReadInt32(HdrSessionInfoUpdate);
        if (update == _lastSessionInfoUpdate) return null;

        int len = view.ReadInt32(HdrSessionInfoLen);
        int offset = view.ReadInt32(HdrSessionInfoOffset);
        var bytes = new byte[len];
        view.ReadArray(offset, bytes, 0, len);

        // Re-check the counter: if iRacing updated mid-copy, try again next poll.
        if (view.ReadInt32(HdrSessionInfoUpdate) != update) return null;
        _lastSessionInfoUpdate = update;

        int end = Array.IndexOf(bytes, (byte)0);
        return Latin1.GetString(bytes, 0, end < 0 ? len : end);
    }

    private void LoadVarHeaders(MemoryMappedViewAccessor view, int numVars)
    {
        int baseOffset = view.ReadInt32(HdrVarHeaderOffset);
        var vars = new Dictionary<string, VarHeader>(numVars, StringComparer.Ordinal);
        var raw = new byte[VarHeaderSize];

        for (int i = 0; i < numVars; i++)
        {
            view.ReadArray(baseOffset + i * VarHeaderSize, raw, 0, VarHeaderSize);
            var header = new VarHeader(
                Name: ReadCString(raw, 16, 32),
                Type: (VarType)BitConverter.ToInt32(raw, 0),
                Offset: BitConverter.ToInt32(raw, 4),
                Count: BitConverter.ToInt32(raw, 8),
                Unit: ReadCString(raw, 112, 32),
                Description: ReadCString(raw, 48, 64));
            vars[header.Name] = header;
        }
        _vars = vars;
    }

    private static string ReadCString(byte[] raw, int offset, int maxLen)
    {
        int end = Array.IndexOf(raw, (byte)0, offset, maxLen);
        return Latin1.GetString(raw, offset, (end < 0 ? offset + maxLen : end) - offset);
    }
}
