using System.Diagnostics;
using AutoEnvPlus.Core.Environment;

namespace AutoEnvPlus.Core.Toolchains;

public enum ToolchainComponent
{
    MsvcBuildTools,
    Llvm,
    MinGw,
    CMake,
    Ninja,
}

public sealed record ExternalToolInstallPlan(
    ToolchainComponent Component,
    string DisplayName,
    string PackageId,
    string Executable,
    IReadOnlyList<string> Arguments,
    bool MayRequireElevation);

public sealed record ExternalToolInstallResult(
    bool Success,
    int ExitCode,
    string StandardOutput,
    string StandardError);

public sealed class WingetToolchainInstaller
{
    public string? FindWinget()
    {
        PathInspectionReport report = new PathInspector().InspectCurrent(["winget"]);
        return report.CommandResolutions
            .FirstOrDefault(resolution => resolution.Command.Equals("winget", StringComparison.OrdinalIgnoreCase))
            ?.Winner?.ExecutablePath;
    }

    public ExternalToolInstallPlan CreatePlan(
        ToolchainComponent component,
        string wingetExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wingetExecutable);
        string executable = Path.GetFullPath(wingetExecutable);
        (string displayName, string packageId, bool mayElevate) = component switch
        {
            ToolchainComponent.MsvcBuildTools => (
                "Visual Studio 2022 Build Tools · C++ workload",
                "Microsoft.VisualStudio.2022.BuildTools",
                true),
            ToolchainComponent.Llvm => ("LLVM/Clang", "LLVM.LLVM", true),
            ToolchainComponent.MinGw => (
                "MinGW-w64 / GCC · WinLibs POSIX + UCRT",
                "BrechtSanders.WinLibs.POSIX.UCRT",
                false),
            ToolchainComponent.CMake => ("CMake", "Kitware.CMake", true),
            ToolchainComponent.Ninja => ("Ninja", "Ninja-build.Ninja", false),
            _ => throw new ArgumentOutOfRangeException(nameof(component)),
        };

        List<string> arguments =
        [
            "install",
            "--exact",
            "--id",
            packageId,
            "--source",
            "winget",
            "--accept-source-agreements",
            "--accept-package-agreements",
            "--disable-interactivity",
            "--silent",
        ];
        if (component == ToolchainComponent.MsvcBuildTools)
        {
            arguments.Add("--override");
            arguments.Add(
                "--wait --passive --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended");
        }

        return new ExternalToolInstallPlan(
            component,
            displayName,
            packageId,
            executable,
            arguments,
            mayElevate);
    }

    public async Task<ExternalToolInstallResult> InstallAsync(
        ExternalToolInstallPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ProcessStartInfo startInfo = new()
        {
            FileName = plan.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            return new ExternalToolInstallResult(false, -1, string.Empty, "WinGet could not be started.");
        }

        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        string standardOutput = await output.ConfigureAwait(false);
        string standardError = await error.ConfigureAwait(false);
        return new ExternalToolInstallResult(
            process.ExitCode == 0,
            process.ExitCode,
            standardOutput,
            standardError);
    }
}
