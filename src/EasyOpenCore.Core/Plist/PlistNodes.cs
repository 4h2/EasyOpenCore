namespace EasyOpenCore.Core.Plist;

/// <summary>A property list value. Dictionaries keep insertion order, as OpenCore and ProperTree do.</summary>
public abstract class PNode
{
    public PDict AsDict => (PDict)this;
    public PArray AsArray => (PArray)this;

    public abstract PNode Clone();
}

public sealed class PDict : PNode
{
    private readonly List<KeyValuePair<string, PNode>> _items = [];

    public IEnumerable<string> Keys => _items.Select(i => i.Key);
    public IEnumerable<KeyValuePair<string, PNode>> Items => _items;
    public int Count => _items.Count;

    public PNode this[string key]
    {
        get => TryGet(key) ?? throw new KeyNotFoundException(key);
        set
        {
            int i = _items.FindIndex(p => p.Key == key);
            if (i >= 0)
                _items[i] = new(key, value);
            else
                _items.Add(new(key, value));
        }
    }

    public PNode? TryGet(string key) => _items.FirstOrDefault(p => p.Key == key).Value;

    public bool ContainsKey(string key) => _items.Exists(p => p.Key == key);

    public bool Remove(string key) => _items.RemoveAll(p => p.Key == key) > 0;

    public void Clear() => _items.Clear();

    /// <summary>Gets a child dictionary, creating it when missing.</summary>
    public PDict Dict(string key)
    {
        if (TryGet(key) is PDict d)
            return d;
        var created = new PDict();
        this[key] = created;
        return created;
    }

    /// <summary>Gets a child array, creating it when missing.</summary>
    public PArray Array(string key)
    {
        if (TryGet(key) is PArray a)
            return a;
        var created = new PArray();
        this[key] = created;
        return created;
    }

    public string? GetString(string key) => (TryGet(key) as PString)?.Value;

    public override PNode Clone()
    {
        var d = new PDict();
        foreach (var (k, v) in _items)
            d[k] = v.Clone();
        return d;
    }
}

public sealed class PArray : PNode
{
    public List<PNode> Items { get; } = [];

    public PArray() { }

    public PArray(IEnumerable<PNode> items) => Items.AddRange(items);

    public override PNode Clone() => new PArray(Items.Select(i => i.Clone()));
}

public sealed class PString(string value) : PNode
{
    public string Value { get; } = value;
    public override PNode Clone() => this;
}

public sealed class PInteger(long value) : PNode
{
    public long Value { get; } = value;
    public override PNode Clone() => this;
}

public sealed class PReal(double value) : PNode
{
    public double Value { get; } = value;
    public override PNode Clone() => this;
}

public sealed class PBool(bool value) : PNode
{
    public bool Value { get; } = value;
    public override PNode Clone() => this;
}

public sealed class PData(byte[] value) : PNode
{
    public byte[] Value { get; } = value;
    public override PNode Clone() => this;
}

public sealed class PDate(DateTime value) : PNode
{
    public DateTime Value { get; } = value;
    public override PNode Clone() => this;
}

/// <summary>Shorthands for building plist values.</summary>
public static class P
{
    public static PString Str(string v) => new(v);
    public static PInteger Int(long v) => new(v);
    public static PBool Bool(bool v) => new(v);
    public static PData Data(byte[] v) => new(v);
    public static PData Hex(string hex) => new(Convert.FromHexString(hex));

    public static PDict Dict(params (string Key, PNode Value)[] items)
    {
        var d = new PDict();
        foreach (var (k, v) in items)
            d[k] = v;
        return d;
    }
}
