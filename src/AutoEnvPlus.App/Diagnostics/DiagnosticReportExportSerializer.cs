using System.Text.Json;
using System.Text.Json.Serialization;
using AutoEnvPlus.Core.Diagnostics;

namespace AutoEnvPlus.App.Diagnostics;

internal static class DiagnosticReportExportSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(EnvironmentDiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(
            new DiagnosticReportExportEnvelope(1, "AutoEnvPlus", report),
            Options);
    }

    private sealed record DiagnosticReportExportEnvelope(
        int SchemaVersion,
        string GeneratedBy,
        EnvironmentDiagnosticReport Report);
}
