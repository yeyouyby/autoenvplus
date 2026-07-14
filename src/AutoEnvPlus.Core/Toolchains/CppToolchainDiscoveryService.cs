using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Toolchains;

public sealed class CppToolchainDiscoveryService
{
    public async Task<CppToolchainDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        List<string> errors = [];
        IReadOnlyList<VisualCppInstallation> visualStudio = await DiscoverVisualStudioAsync(
            errors,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<WindowsSdkInstallation> sdks = DiscoverWindowsSdks(
            FindWindowsKitsRoot(errors));
        RuntimeProbeDefinition[] definitions = RuntimeProbeDefinition.Defaults
            .Where(definition => definition.Kind is RuntimeKind.Llvm
                or RuntimeKind.Mingw
                or RuntimeKind.CMake
                or RuntimeKind.Ninja)
            .ToArray();
        PathInspectionReport pathReport = new PathInspector().InspectCurrentAndPersisted(
            definitions.Select(definition => definition.Command));
        IReadOnlyList<DiscoveredRuntime> tools = await new RuntimeDiscoveryService(definitions)
            .DiscoverAsync(pathReport, cancellationToken)
            .ConfigureAwait(false);
        return new CppToolchainDiscoveryResult(visualStudio, sdks, tools, errors);
    }

    public IReadOnlyList<VisualCppInstallation> ParseVisualStudioInstances(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("vswhere output is not a JSON array.");
        }

        List<VisualCppInstallation> installations = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            string? instanceId = GetString(item, "instanceId");
            string? installationPath = GetString(item, "installationPath");
            if (string.IsNullOrWhiteSpace(instanceId)
                || string.IsNullOrWhiteSpace(installationPath))
            {
                continue;
            }

            string displayName = GetString(item, "displayName") ?? "Visual Studio Build Tools";
            string version = GetString(item, "installationVersion") ?? "unknown";
            if (item.TryGetProperty("catalog", out JsonElement catalog))
            {
                displayName = GetString(catalog, "productDisplayVersion") is string displayVersion
                    ? $"{displayName} {displayVersion}"
                    : displayName;
            }

            string fullPath = Path.GetFullPath(installationPath);
            string toolsVersionFile = Path.Combine(
                fullPath,
                "VC",
                "Auxiliary",
                "Build",
                "Microsoft.VCToolsVersion.default.txt");
            string? toolsVersion = File.Exists(toolsVersionFile)
                ? File.ReadAllText(toolsVersionFile).Trim()
                : null;
            string activationScript = Path.Combine(
                fullPath,
                "VC",
                "Auxiliary",
                "Build",
                "vcvarsall.bat");
            IReadOnlyList<CppArchitecturePair> architecturePairs = DiscoverArchitecturePairs(
                fullPath,
                toolsVersion);

            installations.Add(new VisualCppInstallation(
                instanceId,
                displayName,
                fullPath,
                version,
                string.IsNullOrWhiteSpace(toolsVersion) ? null : toolsVersion,
                File.Exists(activationScript) ? activationScript : null,
                GetBoolean(item, "isComplete"),
                GetBoolean(item, "isLaunchable"),
                architecturePairs));
        }

        return installations
            .OrderByDescending(instance => instance.VisualStudioVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<WindowsSdkInstallation> DiscoverWindowsSdks(string? kitsRoot)
    {
        if (string.IsNullOrWhiteSpace(kitsRoot))
        {
            return [];
        }

        string fullRoot = Path.GetFullPath(kitsRoot);
        string libRoot = Path.Combine(fullRoot, "Lib");
        string includeRoot = Path.Combine(fullRoot, "Include");
        if (!Directory.Exists(libRoot))
        {
            return [];
        }

        List<WindowsSdkInstallation> sdks = [];
        foreach (string versionDirectory in Directory.EnumerateDirectories(libRoot))
        {
            string versionName = Path.GetFileName(versionDirectory);
            if (!RuntimeVersion.TryParse(versionName, out RuntimeVersion? version)
                || !File.Exists(Path.Combine(includeRoot, versionName, "um", "Windows.h")))
            {
                continue;
            }

            List<RuntimeArchitecture> architectures = [];
            if (File.Exists(Path.Combine(versionDirectory, "um", "x64", "kernel32.lib")))
            {
                architectures.Add(RuntimeArchitecture.X64);
            }

            if (File.Exists(Path.Combine(versionDirectory, "um", "x86", "kernel32.lib")))
            {
                architectures.Add(RuntimeArchitecture.X86);
            }

            if (File.Exists(Path.Combine(versionDirectory, "um", "arm64", "kernel32.lib")))
            {
                architectures.Add(RuntimeArchitecture.Arm64);
            }

            if (architectures.Count > 0)
            {
                sdks.Add(new WindowsSdkInstallation(version!, fullRoot, architectures));
            }
        }

        return sdks.OrderByDescending(sdk => sdk.Version).ToArray();
    }

    public CppActivationPlan CreateActivationPlan(
        VisualCppInstallation installation,
        RuntimeArchitecture targetArchitecture,
        RuntimeArchitecture hostArchitecture = RuntimeArchitecture.X64)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.ActivationScript is null)
        {
            throw new InvalidOperationException(
                $"{installation.DisplayName} does not contain vcvarsall.bat.");
        }

        string architectureArgument = (hostArchitecture, targetArchitecture) switch
        {
            (RuntimeArchitecture.X64, RuntimeArchitecture.X64) => "x64",
            (RuntimeArchitecture.X64, RuntimeArchitecture.X86) => "x64_x86",
            (RuntimeArchitecture.X64, RuntimeArchitecture.Arm64) => "x64_arm64",
            (RuntimeArchitecture.X86, RuntimeArchitecture.X86) => "x86",
            (RuntimeArchitecture.X86, RuntimeArchitecture.X64) => "x86_amd64",
            (RuntimeArchitecture.X86, RuntimeArchitecture.Arm64) => "x86_arm64",
            _ => throw new NotSupportedException(
                $"Unsupported MSVC host/target pair: {hostArchitecture} -> {targetArchitecture}"),
        };
        IReadOnlyList<CppArchitecturePair> available = installation.AvailableArchitecturePairs ?? [];
        if (available.Count > 0
            && !available.Any(pair => pair.HostArchitecture == hostArchitecture
                && pair.TargetArchitecture == targetArchitecture))
        {
            throw new InvalidOperationException(
                $"{installation.DisplayName} does not contain the {hostArchitecture} -> {targetArchitecture} compiler tools.");
        }

        string escapedScript = installation.ActivationScript.Replace("\"", "\"\"", StringComparison.Ordinal);
        return new CppActivationPlan(
            $"{installation.DisplayName} · {architectureArgument}",
            hostArchitecture,
            targetArchitecture,
            System.Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            ["/d", "/k", $"call \"{escapedScript}\" {architectureArgument}"],
            installation.InstallationPath);
    }

    public IReadOnlyList<CppArchitecturePair> DiscoverArchitecturePairs(
        string installationPath,
        string? msvcToolsVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationPath);
        if (string.IsNullOrWhiteSpace(msvcToolsVersion))
        {
            return [];
        }

        string toolsRoot = Path.Combine(
            Path.GetFullPath(installationPath),
            "VC",
            "Tools",
            "MSVC",
            msvcToolsVersion,
            "bin");
        (string Host, RuntimeArchitecture HostArchitecture)[] hosts =
        [
            ("Hostx64", RuntimeArchitecture.X64),
            ("Hostx86", RuntimeArchitecture.X86),
        ];
        (string Target, RuntimeArchitecture TargetArchitecture)[] targets =
        [
            ("x64", RuntimeArchitecture.X64),
            ("x86", RuntimeArchitecture.X86),
            ("arm64", RuntimeArchitecture.Arm64),
        ];
        List<CppArchitecturePair> pairs = [];
        foreach ((string host, RuntimeArchitecture hostArchitecture) in hosts)
        {
            foreach ((string target, RuntimeArchitecture targetArchitecture) in targets)
            {
                if (!File.Exists(Path.Combine(toolsRoot, host, target, "cl.exe")))
                {
                    continue;
                }

                string argument = (hostArchitecture, targetArchitecture) switch
                {
                    (RuntimeArchitecture.X64, RuntimeArchitecture.X64) => "x64",
                    (RuntimeArchitecture.X64, RuntimeArchitecture.X86) => "x64_x86",
                    (RuntimeArchitecture.X64, RuntimeArchitecture.Arm64) => "x64_arm64",
                    (RuntimeArchitecture.X86, RuntimeArchitecture.X86) => "x86",
                    (RuntimeArchitecture.X86, RuntimeArchitecture.X64) => "x86_amd64",
                    (RuntimeArchitecture.X86, RuntimeArchitecture.Arm64) => "x86_arm64",
                    _ => throw new InvalidOperationException("Unexpected MSVC architecture pair."),
                };
                pairs.Add(new CppArchitecturePair(
                    hostArchitecture,
                    targetArchitecture,
                    argument));
            }
        }

        return pairs;
    }

    private async Task<IReadOnlyList<VisualCppInstallation>> DiscoverVisualStudioAsync(
        List<string> errors,
        CancellationToken cancellationToken)
    {
        string programFilesX86 = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.ProgramFilesX86);
        string vswhere = Path.Combine(
            programFilesX86,
            "Microsoft Visual Studio",
            "Installer",
            "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return [];
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = vswhere,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[]
        {
            "-products",
            "*",
            "-requires",
            "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
            "-format",
            "json",
            "-utf8",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = new() { StartInfo = startInfo };
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string standardError = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                errors.Add($"vswhere exited with {process.ExitCode}: {standardError.Trim()}");
                return [];
            }

            return ParseVisualStudioInstances(await output.ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or JsonException)
        {
            errors.Add($"Visual Studio detection failed: {exception.Message}");
            return [];
        }
    }

    private static string? FindWindowsKitsRoot(List<string> errors)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? key = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows Kits\Installed Roots",
                    writable: false);
                if (key?.GetValue("KitsRoot10") is string value && Directory.Exists(value))
                {
                    return value;
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                errors.Add($"Windows SDK registry detection failed ({view}): {exception.Message}");
            }
        }

        return null;
    }

    private static string? GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBoolean(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
