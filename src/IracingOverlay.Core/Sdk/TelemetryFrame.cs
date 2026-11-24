namespace IracingOverlay.Core.Sdk;

/// <summary>An immutable copy of one telemetry sample. Safe to hand to another thread.</summary>
public sealed class TelemetryFrame
{
    private readonly byte[] _data;
    private readonly IReadOnlyDictionary<string, VarHeader> _vars;

    public TelemetryFrame(byte[] data, IReadOnlyDictionary<string, VarHeader> vars, int tickCount)
    {
        _data = data;
        _vars = vars;
        TickCount = tickCount;
    }

    public int TickCount { get; }

    public bool Has(string name) => _vars.ContainsKey(name);

    public double GetDouble(string name, int index = 0, double fallback = 0)
    {
        if (!_vars.TryGetValue(name, out var h) || index < 0 || index >= h.Count) return fallback;
        int off = h.Offset + index * h.ElementSize;
        return h.Type switch
        {
            VarType.Char => _data[off],
            VarType.Bool => _data[off] != 0 ? 1 : 0,
            VarType.Int or VarType.BitField => BitConverter.ToInt32(_data, off),
            VarType.Float => BitConverter.ToSingle(_data, off),
            VarType.Double => BitConverter.ToDouble(_data, off),
            _ => fallback,
        };
    }

    public float GetFloat(string name, int index = 0, float fallback = 0) => (float)GetDouble(name, index, fallback);

    public int GetInt(string name, int index = 0, int fallback = 0)
    {
        if (!_vars.TryGetValue(name, out var h) || index < 0 || index >= h.Count) return fallback;
        if (h.Type is VarType.Int or VarType.BitField)
            return BitConverter.ToInt32(_data, h.Offset + index * 4);
        return (int)GetDouble(name, index, fallback);
    }

    public bool GetBool(string name, int index = 0) => GetDouble(name, index) != 0;
}
