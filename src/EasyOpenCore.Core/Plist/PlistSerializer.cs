using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EasyOpenCore.Core.Plist;

/// <summary>Reads and writes XML property lists in the same layout as Apple's tools (tab indentation).</summary>
public static class PlistSerializer
{
    public static PNode Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static PNode Load(Stream stream)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        var root = XDocument.Load(reader).Root ?? throw new FormatException("Empty plist");
        var first = root.Elements().FirstOrDefault() ?? throw new FormatException("plist has no value");
        return Parse(first);
    }

    public static PNode Parse(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return Load(stream);
    }

    private static PNode Parse(XElement e)
    {
        switch (e.Name.LocalName)
        {
            case "dict":
            {
                var dict = new PDict();
                var children = e.Elements().ToList();
                for (int i = 0; i + 1 < children.Count; i += 2)
                {
                    if (children[i].Name.LocalName != "key")
                        throw new FormatException($"Expected <key>, found <{children[i].Name.LocalName}>");
                    dict[children[i].Value] = Parse(children[i + 1]);
                }
                return dict;
            }
            case "array":
                return new PArray(e.Elements().Select(Parse));
            case "string":
                return new PString(e.Value);
            case "integer":
                return new PInteger(ParseInteger(e.Value.Trim()));
            case "real":
                return new PReal(double.Parse(e.Value.Trim(), CultureInfo.InvariantCulture));
            case "true":
                return new PBool(true);
            case "false":
                return new PBool(false);
            case "data":
                return new PData(Convert.FromBase64String(string.Concat(e.Value.Where(c => !char.IsWhiteSpace(c)))));
            case "date":
                return new PDate(DateTime.Parse(e.Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
            default:
                throw new FormatException($"Unknown plist element <{e.Name.LocalName}>");
        }
    }

    private static long ParseInteger(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt64(s[2..], 16)
            : long.Parse(s, CultureInfo.InvariantCulture);

    public static void Save(PNode root, string path) =>
        File.WriteAllText(path, ToXml(root), new UTF8Encoding(false));

    public static string ToXml(PNode root)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n");
        Write(sb, root, 0);
        sb.Append("</plist>\n");
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, PNode node, int depth)
    {
        string pad = new('\t', depth);
        switch (node)
        {
            case PDict { Count: 0 }:
                sb.Append(pad).Append("<dict/>\n");
                break;
            case PDict d:
                sb.Append(pad).Append("<dict>\n");
                foreach (var (k, v) in d.Items)
                {
                    sb.Append(pad).Append('\t').Append("<key>").Append(Escape(k)).Append("</key>\n");
                    Write(sb, v, depth + 1);
                }
                sb.Append(pad).Append("</dict>\n");
                break;
            case PArray { Items.Count: 0 }:
                sb.Append(pad).Append("<array/>\n");
                break;
            case PArray a:
                sb.Append(pad).Append("<array>\n");
                foreach (var item in a.Items)
                    Write(sb, item, depth + 1);
                sb.Append(pad).Append("</array>\n");
                break;
            case PString s:
                sb.Append(pad).Append("<string>").Append(Escape(s.Value)).Append("</string>\n");
                break;
            case PInteger i:
                sb.Append(pad).Append("<integer>").Append(i.Value.ToString(CultureInfo.InvariantCulture)).Append("</integer>\n");
                break;
            case PReal r:
                sb.Append(pad).Append("<real>").Append(r.Value.ToString("R", CultureInfo.InvariantCulture)).Append("</real>\n");
                break;
            case PBool b:
                sb.Append(pad).Append(b.Value ? "<true/>\n" : "<false/>\n");
                break;
            case PData data:
                sb.Append(pad).Append("<data>").Append(Convert.ToBase64String(data.Value)).Append("</data>\n");
                break;
            case PDate date:
                sb.Append(pad).Append("<date>").Append(date.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")).Append("</date>\n");
                break;
            default:
                throw new InvalidOperationException($"Unsupported node {node.GetType().Name}");
        }
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
