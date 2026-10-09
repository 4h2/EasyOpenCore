using System.Text;

namespace EasyOpenCore.Core.Acpi;

/// <summary>
/// Minimal ACPI Machine Language encoder for the small SSDTs OpenCore needs. Every node
/// emits AML bytes and the equivalent ASL source, so the generated .aml can be audited.
/// Opcodes follow the ACPI 6.x specification, chapter 20 ("ACPI Machine Language Specification").
/// </summary>
public abstract class AmlNode
{
    public abstract void Emit(List<byte> o);
    public abstract void Asl(AslWriter w);
}

/// <summary>An expression (TermArg) that can also be rendered inline in ASL.</summary>
public abstract class AmlExpr : AmlNode
{
    public abstract string AslExpr();
    public override void Asl(AslWriter w) => w.Line(AslExpr());
}

public sealed class AslWriter
{
    private readonly StringBuilder _sb = new();
    private int _indent;

    public void Line(string text) => _sb.Append(new string(' ', _indent * 4)).Append(text).Append('\n');

    public void Block(string header, IEnumerable<AmlNode> body)
    {
        Line(header);
        Line("{");
        _indent++;
        foreach (var n in body)
            n.Asl(this);
        _indent--;
        Line("}");
    }

    public override string ToString() => _sb.ToString();
}

public static class AmlEncoding
{
    /// <summary>Wraps a body with its PkgLength prefix (the length includes the prefix itself).</summary>
    public static byte[] WithPkgLength(List<byte> body)
    {
        int n = body.Count;
        byte[] prefix;
        if (n + 1 <= 0x3F)
            prefix = [(byte)(n + 1)];
        else if (n + 2 <= 0xFFF)
        {
            int t = n + 2;
            prefix = [(byte)(0x40 | (t & 0xF)), (byte)(t >> 4)];
        }
        else if (n + 3 <= 0xFFFFF)
        {
            int t = n + 3;
            prefix = [(byte)(0x80 | (t & 0xF)), (byte)(t >> 4), (byte)(t >> 12)];
        }
        else
        {
            int t = n + 4;
            prefix = [(byte)(0xC0 | (t & 0xF)), (byte)(t >> 4), (byte)(t >> 12), (byte)(t >> 20)];
        }
        return [.. prefix, .. body];
    }

    /// <summary>"\_SB.PCI0.EC" → RootChar + MultiNamePrefix + padded NameSegs.</summary>
    public static byte[] NameString(string path)
    {
        var o = new List<byte>();
        int i = 0;
        if (path.StartsWith('\\'))
        {
            o.Add((byte)'\\');
            i = 1;
        }
        while (i < path.Length && path[i] == '^')
        {
            o.Add((byte)'^');
            i++;
        }
        var segs = path[i..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        switch (segs.Length)
        {
            case 0: o.Add(0x00); break;
            case 1: break;
            case 2: o.Add(0x2E); break;
            default: o.Add(0x2F); o.Add((byte)segs.Length); break;
        }
        foreach (var s in segs)
            o.AddRange(NameSeg(s));
        return [.. o];
    }

    public static byte[] NameSeg(string seg)
    {
        if (seg.Length is 0 or > 4)
            throw new ArgumentException($"Invalid ACPI name segment '{seg}'");
        return Encoding.ASCII.GetBytes(seg.ToUpperInvariant().PadRight(4, '_'));
    }
}

// ---------- Expressions ----------

public sealed class AmlInt(ulong value) : AmlExpr
{
    public ulong Value { get; } = value;

    public override void Emit(List<byte> o)
    {
        switch (Value)
        {
            case 0: o.Add(0x00); break;
            case 1: o.Add(0x01); break;
            case <= 0xFF: o.Add(0x0A); o.Add((byte)Value); break;
            case <= 0xFFFF: o.Add(0x0B); o.AddRange(BitConverter.GetBytes((ushort)Value)); break;
            case <= 0xFFFFFFFF: o.Add(0x0C); o.AddRange(BitConverter.GetBytes((uint)Value)); break;
            default: o.Add(0x0E); o.AddRange(BitConverter.GetBytes(Value)); break;
        }
    }

    public override string AslExpr() => Value switch
    {
        0 => "Zero",
        1 => "One",
        _ => $"0x{Value:X2}",
    };
}

public sealed class AmlString(string value) : AmlExpr
{
    public override void Emit(List<byte> o)
    {
        o.Add(0x0D);
        o.AddRange(Encoding.ASCII.GetBytes(value));
        o.Add(0x00);
    }

    public override string AslExpr() => $"\"{value}\"";
}

/// <summary>EisaId ("PNP0C09") → compressed DWord.</summary>
public sealed class AmlEisaId(string id) : AmlExpr
{
    public override void Emit(List<byte> o)
    {
        if (id.Length != 7)
            throw new ArgumentException($"Invalid EISA id {id}");
        int mfg = ((id[0] - 0x40) & 0x1F) << 10 | ((id[1] - 0x40) & 0x1F) << 5 | ((id[2] - 0x40) & 0x1F);
        int prod = Convert.ToInt32(id[3..], 16);
        o.Add(0x0C);
        o.Add((byte)(mfg >> 8));
        o.Add((byte)mfg);
        o.Add((byte)(prod >> 8));
        o.Add((byte)prod);
    }

    public override string AslExpr() => $"EisaId (\"{id}\")";
}

public sealed class AmlBuffer(byte[] bytes, string? aslOverride = null) : AmlExpr
{
    public override void Emit(List<byte> o)
    {
        var body = new List<byte>();
        new AmlInt((ulong)bytes.Length).Emit(body);
        body.AddRange(bytes);
        o.Add(0x11);
        o.AddRange(AmlEncoding.WithPkgLength(body));
    }

    public override string AslExpr() =>
        aslOverride ?? $"Buffer (0x{bytes.Length:X2}) {{ {string.Join(", ", bytes.Select(b => $"0x{b:X2}"))} }}";
}

public sealed class AmlPackage(params AmlExpr[] items) : AmlExpr
{
    public override void Emit(List<byte> o)
    {
        var body = new List<byte> { (byte)items.Length };
        foreach (var i in items)
            i.Emit(body);
        o.Add(0x12);
        o.AddRange(AmlEncoding.WithPkgLength(body));
    }

    public override string AslExpr() => $"Package (0x{items.Length:X2}) {{ {string.Join(", ", items.Select(i => i.AslExpr()))} }}";
}

public sealed class AmlArg(int index) : AmlExpr
{
    public override void Emit(List<byte> o) => o.Add((byte)(0x68 + index));
    public override string AslExpr() => $"Arg{index}";
}

public sealed class AmlLocal(int index) : AmlExpr
{
    public override void Emit(List<byte> o) => o.Add((byte)(0x60 + index));
    public override string AslExpr() => $"Local{index}";
}

public sealed class AmlNameRef(string path) : AmlExpr
{
    public string Path { get; } = path;
    public override void Emit(List<byte> o) => o.AddRange(AmlEncoding.NameString(Path));
    public override string AslExpr() => Path;
}

public sealed class AmlLNot(AmlExpr operand) : AmlExpr
{
    public override void Emit(List<byte> o) { o.Add(0x92); operand.Emit(o); }
    public override string AslExpr() => $"LNot ({operand.AslExpr()})";
}

public sealed class AmlLEqual(AmlExpr a, AmlExpr b) : AmlExpr
{
    public override void Emit(List<byte> o) { o.Add(0x93); a.Emit(o); b.Emit(o); }
    public override string AslExpr() => $"LEqual ({a.AslExpr()}, {b.AslExpr()})";
}

/// <summary>LNotEqual is encoded as LNot(LEqual(a, b)).</summary>
public sealed class AmlLNotEqual(AmlExpr a, AmlExpr b) : AmlExpr
{
    public override void Emit(List<byte> o) { o.Add(0x92); o.Add(0x93); a.Emit(o); b.Emit(o); }
    public override string AslExpr() => $"LNotEqual ({a.AslExpr()}, {b.AslExpr()})";
}

/// <summary>Method invocation; also usable as a statement.</summary>
public sealed class AmlCall(string path, params AmlExpr[] args) : AmlExpr
{
    public override void Emit(List<byte> o)
    {
        o.AddRange(AmlEncoding.NameString(path));
        foreach (var a in args)
            a.Emit(o);
    }

    public override string AslExpr() => $"{path} ({string.Join(", ", args.Select(a => a.AslExpr()))})";
}

public sealed class AmlCondRefOf(string path) : AmlExpr
{
    public override void Emit(List<byte> o)
    {
        o.Add(0x5B);
        o.Add(0x12);
        o.AddRange(AmlEncoding.NameString(path));
        o.Add(0x00); // no target
    }

    public override string AslExpr() => $"CondRefOf ({path})";
}

public enum MatchOp : byte { MTR = 0, MEQ = 1, MLE = 2, MLT = 3, MGE = 4, MGT = 5 }

public sealed class AmlMatch(AmlExpr package, MatchOp op1, AmlExpr operand1, MatchOp op2, AmlExpr operand2, AmlExpr start) : AmlExpr
{
    public override void Emit(List<byte> o)
    {
        o.Add(0x89);
        package.Emit(o);
        o.Add((byte)op1);
        operand1.Emit(o);
        o.Add((byte)op2);
        operand2.Emit(o);
        start.Emit(o);
    }

    public override string AslExpr() =>
        $"Match ({package.AslExpr()}, {op1}, {operand1.AslExpr()}, {op2}, {operand2.AslExpr()}, {start.AslExpr()})";
}

/// <summary>The value 0xFFFFFFFFFFFFFFFF ("Ones").</summary>
public sealed class AmlOnes : AmlExpr
{
    public override void Emit(List<byte> o) => o.Add(0xFF);
    public override string AslExpr() => "Ones";
}

// ---------- Statements / named objects ----------

public enum ExternalType : byte
{
    Unknown = 0, IntObj = 1, StrObj = 2, BuffObj = 3, PkgObj = 4, FieldUnitObj = 5, DeviceObj = 6,
    EventObj = 7, MethodObj = 8, MutexObj = 9, OpRegionObj = 10, PowerResObj = 11, ProcessorObj = 12,
}

public sealed class AmlExternal(string path, ExternalType type, int argCount = 0) : AmlNode
{
    public override void Emit(List<byte> o)
    {
        o.Add(0x15);
        o.AddRange(AmlEncoding.NameString(path));
        o.Add((byte)type);
        o.Add((byte)argCount);
    }

    public override void Asl(AslWriter w) => w.Line($"External ({path.TrimStart('\\')}, {type})");
}

public sealed class AmlScope(string path, params AmlNode[] body) : AmlNode
{
    public override void Emit(List<byte> o)
    {
        var inner = new List<byte>(AmlEncoding.NameString(path));
        foreach (var n in body) n.Emit(inner);
        o.Add(0x10);
        o.AddRange(AmlEncoding.WithPkgLength(inner));
    }

    public override void Asl(AslWriter w) => w.Block($"Scope ({path})", body);
}

public sealed class AmlDevice(string name, params AmlNode[] body) : AmlNode
{
    public override void Emit(List<byte> o)
    {
        var inner = new List<byte>(AmlEncoding.NameString(name));
        foreach (var n in body) n.Emit(inner);
        o.Add(0x5B);
        o.Add(0x82);
        o.AddRange(AmlEncoding.WithPkgLength(inner));
    }

    public override void Asl(AslWriter w) => w.Block($"Device ({name})", body);
}

public sealed class AmlProcessor(string name, byte procId, uint pblkAddress, byte pblkLength, params AmlNode[] body) : AmlNode
{
    public override void Emit(List<byte> o)
    {
        var inner = new List<byte>(AmlEncoding.NameString(name)) { procId };
        inner.AddRange(BitConverter.GetBytes(pblkAddress));
        inner.Add(pblkLength);
        foreach (var n in body) n.Emit(inner);
        o.Add(0x5B);
        o.Add(0x83);
        o.AddRange(AmlEncoding.WithPkgLength(inner));
    }

    public override void Asl(AslWriter w) =>
        w.Block($"Processor ({name}, 0x{procId:X2}, 0x{pblkAddress:X8}, 0x{pblkLength:X2})", body);
}

public sealed class AmlMethod(string name, int argCount, params AmlNode[] body) : AmlNode
{
    public bool Serialized { get; init; }

    public override void Emit(List<byte> o)
    {
        var inner = new List<byte>(AmlEncoding.NameString(name)) { (byte)((argCount & 7) | (Serialized ? 0x08 : 0)) };
        foreach (var n in body) n.Emit(inner);
        o.Add(0x14);
        o.AddRange(AmlEncoding.WithPkgLength(inner));
    }

    public override void Asl(AslWriter w) =>
        w.Block($"Method ({name}, {argCount}, {(Serialized ? "Serialized" : "NotSerialized")})", body);
}

public sealed class AmlName(string name, AmlExpr value) : AmlNode
{
    public override void Emit(List<byte> o)
    {
        o.Add(0x08);
        o.AddRange(AmlEncoding.NameString(name));
        value.Emit(o);
    }

    public override void Asl(AslWriter w) => w.Line($"Name ({name}, {value.AslExpr()})");
}

public sealed class AmlIf(AmlExpr predicate, AmlNode[] then, AmlNode[]? otherwise = null) : AmlNode
{
    public override void Emit(List<byte> o)
    {
        var inner = new List<byte>();
        predicate.Emit(inner);
        foreach (var n in then) n.Emit(inner);
        o.Add(0xA0);
        o.AddRange(AmlEncoding.WithPkgLength(inner));

        if (otherwise is null)
            return;
        var elseBody = new List<byte>();
        foreach (var n in otherwise) n.Emit(elseBody);
        o.Add(0xA1);
        o.AddRange(AmlEncoding.WithPkgLength(elseBody));
    }

    public override void Asl(AslWriter w)
    {
        w.Block($"If ({predicate.AslExpr()})", then);
        if (otherwise is not null)
            w.Block("Else", otherwise);
    }
}

public sealed class AmlReturn(AmlExpr value) : AmlNode
{
    public override void Emit(List<byte> o) { o.Add(0xA4); value.Emit(o); }
    public override void Asl(AslWriter w) => w.Line($"Return ({value.AslExpr()})");
}

public sealed class AmlStore(AmlExpr source, AmlExpr target) : AmlNode
{
    public override void Emit(List<byte> o) { o.Add(0x70); source.Emit(o); target.Emit(o); }
    public override void Asl(AslWriter w) => w.Line($"Store ({source.AslExpr()}, {target.AslExpr()})");
}

/// <summary>A complete SSDT ("DefinitionBlock").</summary>
public sealed class AmlTable(string oemTableId, params AmlNode[] body)
{
    public const string OemId = "EOCORE";
    private const uint OemRevision = 0x00001000;

    public string OemTableId { get; } = oemTableId;

    public byte[] Build()
    {
        var content = new List<byte>();
        foreach (var n in body) n.Emit(content);

        var t = new List<byte>(36 + content.Count);
        t.AddRange(Encoding.ASCII.GetBytes("SSDT"));
        t.AddRange(BitConverter.GetBytes((uint)(36 + content.Count)));
        t.Add(2);   // revision 2: 64-bit integers
        t.Add(0);   // checksum, fixed below
        t.AddRange(Encoding.ASCII.GetBytes(OemId.PadRight(6)[..6]));
        t.AddRange(Encoding.ASCII.GetBytes(OemTableId.PadRight(8)[..8]));
        t.AddRange(BitConverter.GetBytes(OemRevision));
        t.AddRange(Encoding.ASCII.GetBytes("EOC "));
        t.AddRange(BitConverter.GetBytes(1u));
        t.AddRange(content);

        byte sum = 0;
        foreach (var b in t) sum += b;
        t[9] = (byte)(0x100 - sum);
        return [.. t];
    }

    public string ToAsl()
    {
        var w = new AslWriter();
        w.Block($"DefinitionBlock (\"\", \"SSDT\", 2, \"{OemId}\", \"{OemTableId}\", 0x{OemRevision:X8})", body);
        return "// Generated by EasyOpenCore\n" + w;
    }
}
