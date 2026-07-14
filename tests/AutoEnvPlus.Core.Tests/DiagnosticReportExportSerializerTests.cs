using System.Text.Json;
using AutoEnvPlus.App.Diagnostics;
using AutoEnvPlus.Core.Diagnostics;
using AutoEnvPlus.Core.Environment;

namespace AutoEnvPlus.Core.Tests;

public sealed class DiagnosticReportExportSerializerTests
{
    [Fact]
    public void Serialize_UsesStableEnvelopeAndStringEnums()
    {
        DateTimeOffset createdAt = new(2026, 7, 14, 8, 30, 0, TimeSpan.Zero);
        EnvironmentDiagnosticReport report = new(
            createdAt,
            new PathInspectionReport([], []),
            [new DiagnosticIssue(
                "path.missing",
                DiagnosticSeverity.Warning,
                "Missing path",
                "The configured directory is unavailable.",
                "D:\\tools")],
            [new DiagnosticCommandStatus("python", "D:\\Python\\python.exe", 1, false)],
            [],
            [],
            2);

        string json = DiagnosticReportExportSerializer.Serialize(report);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("AutoEnvPlus", root.GetProperty("generatedBy").GetString());
        JsonElement exportedReport = root.GetProperty("report");
        Assert.Equal(createdAt, exportedReport.GetProperty("createdAtUtc").GetDateTimeOffset());
        Assert.Equal(2, exportedReport.GetProperty("managedRuntimeCount").GetInt32());
        Assert.Equal(
            "Warning",
            exportedReport.GetProperty("issues")[0].GetProperty("severity").GetString());
        Assert.Equal(
            "D:\\Python\\python.exe",
            exportedReport.GetProperty("commands")[0].GetProperty("winnerPath").GetString());
    }

    [Fact]
    public void Serialize_RejectsNullReport() => Assert.Throws<ArgumentNullException>(
        () => DiagnosticReportExportSerializer.Serialize(null!));
}
