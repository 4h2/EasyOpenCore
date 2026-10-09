using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyOpenCore.Core.Models;

namespace EasyOpenCore.Core;

public static class ReportSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(HardwareReport report) => JsonSerializer.Serialize(report, Options);

    public static HardwareReport? FromJson(string json) => JsonSerializer.Deserialize<HardwareReport>(json, Options);
}
