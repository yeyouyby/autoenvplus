using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Diagnostics;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Providers.Java;
using AutoEnvPlus.Core.Providers.NodeJs;
using AutoEnvPlus.Core.Providers.Python;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.Shell;
using AutoEnvPlus.Core.State;
using AutoEnvPlus.Core.Storage;
using AutoEnvPlus.Core.Toolchains;

using CancellationTokenSource applicationCancellation = new();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    applicationCancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    return await DispatchAsync(args, applicationCancellation.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operation cancelled.");
    return 130;
}
catch (Exception exception) when (exception is IOException
    or InvalidDataException
    or HttpRequestException
    or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

static async Task<int> DispatchAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0)
    {
        return ShowHelp();
    }

    string[] commandArgs = args.Skip(1).ToArray();
    return args[0].ToLowerInvariant() switch
    {
        "doctor" => await RunDoctorAsync(commandArgs, cancellationToken),
        "list" => await RunListAsync(commandArgs, cancellationToken),
        "catalog" => await RunCatalogAsync(commandArgs, cancellationToken),
        "install" => await RunInstallAsync(commandArgs, cancellationToken),
        "uninstall" => await RunUninstallAsync(commandArgs, cancellationToken),
        "use" => await RunUseAsync(commandArgs, cancellationToken),
        "which" => await RunWhichAsync(commandArgs, cancellationToken),
        "exec" => await RunExecAsync(commandArgs, cancellationToken),
        "tool" => await RunToolAsync(commandArgs, cancellationToken),
        "shim" => await RunShimAsync(commandArgs, cancellationToken),
        "shell" => await RunShellAsync(commandArgs, cancellationToken),
        "storage" => await RunStorageAsync(commandArgs, cancellationToken),
        "toolchain" => await RunToolchainAsync(commandArgs, cancellationToken),
        "project" => await RunProjectAsync(commandArgs, cancellationToken),
        "resolve" => RunResolve(commandArgs),
        "help" or "--help" or "-h" => ShowHelp(),
        _ => UnknownCommand(args[0]),
    };
}

static async Task<int> RunDoctorAsync(
    string[] args,
    CancellationToken cancellationToken)
{
    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    EnvironmentDiagnosticReport report = await new EnvironmentDiagnosticService(managedRoot!)
        .InspectCurrentAsync(cancellationToken);
    if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(JsonSerializer.Serialize(report, CreateJsonOptions()));
        return report.IsHealthy ? 0 : 2;
    }

    Console.WriteLine("AutoEnvPlus environment diagnosis");
    Console.WriteLine($"PATH entries: {report.Path.Entries.Count}");
    Console.WriteLine(
        $"Missing: {report.Path.MissingCount}; duplicates: {report.Path.DuplicateCount}; command conflicts: {report.Path.Conflicts.Count}");
    Console.WriteLine(
        $"Managed runtimes: {report.ManagedRuntimeCount}; errors: {report.ErrorCount}; warnings: {report.WarningCount}");
    foreach (DiagnosticIssue issue in report.Issues)
    {
        Console.WriteLine($"  [{issue.Severity.ToString().ToLowerInvariant()}] {issue.Title}");
        Console.WriteLine($"    {issue.Detail}");
        if (!string.IsNullOrWhiteSpace(issue.Path))
        {
            Console.WriteLine($"    {issue.Path}");
        }
    }

    foreach (DiagnosticCommandStatus command in report.Commands.Where(command => command.WinnerPath is not null))
    {
        Console.WriteLine(
            $"  [command] {command.Command} -> {command.WinnerPath} ({command.CandidateCount} candidate(s))");
    }

    Console.WriteLine(report.IsHealthy
        ? "No environment issues found."
        : "Run with --json for the complete machine-readable report.");
    return report.IsHealthy ? 0 : 2;
}

static async Task<int> RunListAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Contains("--managed", StringComparer.OrdinalIgnoreCase))
    {
        if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
        {
            Console.Error.WriteLine(rootError);
            return 1;
        }

        RegistryLoadResult registry = await new ManagedRuntimeRegistry(managedRoot!).LoadAsync(cancellationToken);
        if (registry.Errors.Count > 0)
        {
            foreach (string error in registry.Errors)
            {
                Console.Error.WriteLine(error);
            }

            return 2;
        }

        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(registry.Entries, CreateJsonOptions()));
            return 0;
        }

        Console.WriteLine("AutoEnvPlus managed runtimes");
        foreach (ManagedRuntimeEntry entry in registry.Entries)
        {
            string state = File.Exists(entry.ExecutablePath) ? "ok" : "missing";
            Console.WriteLine(
                $"  {entry.Id,-36} {entry.Kind,-8} {entry.Version,-18} {entry.Architecture,-5} [{state}]");
            Console.WriteLine($"    {entry.InstallRoot}");
        }

        if (registry.Entries.Count == 0)
        {
            Console.WriteLine("  No managed runtimes are registered.");
        }

        return 0;
    }

    IReadOnlyList<DiscoveredRuntime> runtimes = await new RuntimeDiscoveryService().DiscoverCurrentAsync(
        cancellationToken);
    if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(JsonSerializer.Serialize(runtimes, CreateJsonOptions()));
        return 0;
    }

    Console.WriteLine("Discovered runtimes on PATH");
    foreach (DiscoveredRuntime runtime in runtimes)
    {
        string version = runtime.Version?.ToString() ?? "unknown";
        Console.WriteLine($"  {runtime.Kind,-8} {version,-16} {runtime.ExecutablePath}");
        if (!runtime.IsHealthy)
        {
            Console.WriteLine($"    {runtime.Error ?? "unhealthy"}");
        }
    }

    if (runtimes.Count == 0)
    {
        Console.WriteLine("  No known runtime commands were found on PATH.");
    }

    return 0;
}

static async Task<int> RunCatalogAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0
        || (!args[0].Equals("python", StringComparison.OrdinalIgnoreCase)
            && !args[0].Equals("node", StringComparison.OrdinalIgnoreCase)
            && !args[0].Equals("java", StringComparison.OrdinalIgnoreCase)))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus catalog <python|node|java> [--feature java-major] [--lts] [--arch x64|x86|arm64] [--limit count] [--asset version] [--json]");
        return 1;
    }

    if (!TryParseArchitecture(args, out RuntimeArchitecture architecture, out string? architectureError))
    {
        Console.Error.WriteLine(architectureError);
        return 1;
    }

    int limit = 10;
    if (TryFindOptionIndex(args, "--limit", out int limitIndex)
        && (limitIndex + 1 >= args.Length
            || !int.TryParse(args[limitIndex + 1], out limit)
            || limit is < 1 or > 100))
    {
        Console.Error.WriteLine("--limit must be an integer from 1 through 100.");
        return 1;
    }

    using HttpClient client = CreateHttpClient(TimeSpan.FromSeconds(30));
    IRuntimeCatalogProvider? provider = CreateProvider(args[0], client, architecture, args, null, out string? error);
    if (provider is null)
    {
        Console.Error.WriteLine(error);
        return 1;
    }

    IReadOnlyList<RuntimeRelease> catalog = await provider.GetReleasesAsync(cancellationToken);
    IEnumerable<RuntimeRelease> query = catalog.Where(release => release.Architecture == architecture);
    if (args.Contains("--lts", StringComparer.OrdinalIgnoreCase))
    {
        query = query.Where(release => release.Channels.Contains("lts", StringComparer.OrdinalIgnoreCase));
    }

    if (TryFindOptionIndex(args, "--asset", out int assetIndex))
    {
        if (assetIndex + 1 >= args.Length
            || !RuntimeVersion.TryParse(args[assetIndex + 1], out RuntimeVersion? requestedVersion))
        {
            Console.Error.WriteLine("--asset requires an exact runtime version.");
            return 1;
        }

        RuntimeRelease? selected = query.FirstOrDefault(release =>
            release.Version.CompareTo(requestedVersion) == 0);
        if (selected is null)
        {
            Console.Error.WriteLine(
                $"{provider.Kind} {requestedVersion} ({architecture}) is not present in the selected catalog.");
            return 2;
        }

        RuntimePackageAsset asset = await provider.GetAssetAsync(selected, cancellationToken);
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(asset, CreateJsonOptions()));
        }
        else
        {
            Console.WriteLine($"Asset:   {asset.FileName}");
            Console.WriteLine($"URL:     {asset.DownloadUri}");
            Console.WriteLine($"SHA-256: {asset.Sha256}");
            Console.WriteLine($"Root:    {asset.ArchiveRootDirectory}");
            foreach (PackageSignatureVerification signature in asset.SignatureVerifications)
            {
                Console.WriteLine(
                    $"Signed:  {signature.Kind} {signature.HashAlgorithm} · {signature.PrimaryKeyFingerprint}");
                if (signature.Kind == PackageSignatureVerificationKind.SigstoreBundle)
                {
                    Console.WriteLine($"Identity: {signature.CertificateIdentity}");
                    Console.WriteLine($"Issuer:   {signature.CertificateOidcIssuer}");
                    Console.WriteLine(
                        $"Rekor:    index {signature.TransparencyLogIndex} · tree {signature.TransparencyLogTreeSize} · {signature.TransparencyLogId}");
                    Console.WriteLine($"Trust:    SHA-256 {signature.TrustRootSha256} · {signature.KeySourceUri}");
                    Console.WriteLine($"Policy:   {signature.IdentityPolicyUri}");
                }
                else
                {
                    Console.WriteLine($"Key ID:  {signature.SigningKeyId} · {signature.SignerTrust}");
                }

                Console.WriteLine($"Content: {signature.SignedContentUri ?? signature.SignatureUri}");
                Console.WriteLine($"Source:  {signature.SignatureUri}");
            }

            if (asset.SignatureRequirement is PackageSignatureRequirement requirement)
            {
                Console.WriteLine(
                    $"Required: {requirement.Kind} during install · {requirement.ExpectedPrimaryKeyFingerprint}");
                Console.WriteLine($"Signature: {requirement.SignatureUri}");
                Console.WriteLine($"Key:       {requirement.KeySourceUri}");
            }
        }

        return 0;
    }

    RuntimeRelease[] releases = query.Take(limit).ToArray();
    if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(JsonSerializer.Serialize(releases, CreateJsonOptions()));
        return 0;
    }

    Console.WriteLine($"{provider.Id} releases ({architecture})");
    foreach (RuntimeRelease release in releases)
    {
        string channels = string.Join(", ", release.Channels);
        string security = release.IsSecurityRelease ? " security" : string.Empty;
        Console.WriteLine(
            $"  {release.ProviderVersion,-18} {release.ReleaseDate:yyyy-MM-dd}  {channels}{security}");
    }

    return 0;
}

static async Task<int> RunInstallAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length < 2
        || !RuntimeVersion.TryParse(args[1], out RuntimeVersion? requestedVersion))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus install <python|node|java> <exact-version> [--arch x64|x86|arm64] [--root directory] [--yes]");
        return 1;
    }

    if (!TryParseArchitecture(args, out RuntimeArchitecture architecture, out string? architectureError))
    {
        Console.Error.WriteLine(architectureError);
        return 1;
    }

    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    using HttpClient client = CreateHttpClient(TimeSpan.FromMinutes(10));
    IRuntimeCatalogProvider? catalogProvider = CreateProvider(
        args[0],
        client,
        architecture,
        args,
        requestedVersion,
        out string? providerError);
    if (catalogProvider is not IArchiveRuntimeProvider provider)
    {
        Console.Error.WriteLine(providerError ?? "No archive provider is available.");
        return 1;
    }

    IReadOnlyList<RuntimeRelease> catalog = await provider.GetReleasesAsync(cancellationToken);
    RuntimeRelease? release = catalog.FirstOrDefault(item =>
        item.Architecture == architecture
        && item.Version.CompareTo(requestedVersion) == 0);
    if (release is null)
    {
        Console.Error.WriteLine(
            $"{provider.Kind} {requestedVersion} ({architecture}) is not available from {provider.Id}.");
        return 2;
    }

    RuntimePackageAsset asset = await provider.GetAssetAsync(release, cancellationToken);
    ArchiveInstallPlan plan = provider.CreateInstallPlan(asset, managedRoot!);
    Console.WriteLine("Install plan");
    Console.WriteLine($"  Runtime:     {release.Kind} {release.Version} ({release.Architecture})");
    Console.WriteLine($"  Provider:    {release.ProviderId}");
    Console.WriteLine($"  Download:    {asset.DownloadUri}");
    Console.WriteLine($"  SHA-256:     {asset.Sha256}");
    foreach (PackageVerification verification in asset.Verifications)
    {
        Console.WriteLine(
            $"  Verified by: {verification.Kind} {verification.Algorithm} from {verification.SourceUri}");
    }

    foreach (PackageSignatureVerification signature in asset.SignatureVerifications)
    {
        Console.WriteLine(
            $"  Signature:   {signature.Kind} {signature.HashAlgorithm} · {signature.PrimaryKeyFingerprint}");
        if (signature.Kind == PackageSignatureVerificationKind.SigstoreBundle)
        {
            Console.WriteLine($"  Identity:    {signature.CertificateIdentity}");
            Console.WriteLine($"  OIDC issuer: {signature.CertificateOidcIssuer}");
            Console.WriteLine(
                $"  Rekor:       index {signature.TransparencyLogIndex} · tree {signature.TransparencyLogTreeSize} · {signature.TransparencyLogId}");
            Console.WriteLine($"  Trust root:  SHA-256 {signature.TrustRootSha256}");
            Console.WriteLine($"  Policy:      {signature.IdentityPolicyUri}");
        }
        else
        {
            Console.WriteLine($"  Signing key: {signature.SigningKeyId} · {signature.SignerTrust}");
        }

        Console.WriteLine($"  Signed at:   {signature.CreatedAtUtc:O}");
        Console.WriteLine($"  Signed content: {signature.SignedContentUri ?? signature.SignatureUri}");
        Console.WriteLine($"  Signature URI: {signature.SignatureUri}");
        Console.WriteLine($"  Key source:    {signature.KeySourceUri}");
    }

    if (asset.SignatureRequirement is PackageSignatureRequirement signatureRequirement)
    {
        Console.WriteLine(
            $"  Required signature: {signatureRequirement.Kind} during install");
        Console.WriteLine(
            $"  Pinned fingerprint: {signatureRequirement.ExpectedPrimaryKeyFingerprint}");
        Console.WriteLine($"  Signature URI:     {signatureRequirement.SignatureUri}");
        Console.WriteLine($"  Key source:        {signatureRequirement.KeySourceUri}");
    }

    Console.WriteLine($"  Destination: {plan.DestinationRoot}");
    if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine("Preview only: no files were changed. Add --yes to execute this exact plan.");
        return 0;
    }

    ManagedRuntimeEntry entry = new(
        $"{release.Kind.ToString().ToLowerInvariant()}-{release.Version}-{release.Architecture.ToString().ToLowerInvariant()}",
        release.ProviderId,
        release.Kind,
        release.Version,
        release.Architecture,
        plan.DestinationRoot,
        plan.ExpectedExecutableRelativePath,
        asset.Sha256,
        DateTimeOffset.UtcNow,
        release.Channels);
    ManagedRuntimeInstallTransactionResult result = await new ManagedRuntimeInstallCoordinator(
        managedRoot!,
        client).InstallAsync(
            new ManagedRuntimeInstallRequest(plan, entry, SetGlobalDefault: false),
            new ConsoleInstallProgress(),
            cancellationToken);
    if (!result.Success)
    {
        Console.Error.WriteLine($"Install failed: {result.Error}");
        if (result.PendingCleanup)
        {
            Console.Error.WriteLine($"Manual cleanup may be required: {result.InstallRoot}");
        }

        return 2;
    }

    Console.WriteLine(result.InstallOutcome == InstallOutcome.AlreadyInstalled
        ? $"Already installed and registered: {entry.Id}"
        : $"Installed and registered: {entry.Id}");
    return 0;
}

static async Task<int> RunUninstallAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus uninstall <managed-runtime-id> [--root directory] [--force] [--yes]");
        return 1;
    }

    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    ManagedRuntimeUninstaller uninstaller = new(managedRoot!);
    ManagedRuntimeUninstallPlan plan;
    try
    {
        plan = await uninstaller.CreatePlanAsync(args[0], cancellationToken);
    }
    catch (Exception exception) when (exception is KeyNotFoundException or InvalidDataException)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }

    Console.WriteLine("Managed runtime uninstall plan");
    Console.WriteLine($"  Runtime: {plan.Runtime.Id}");
    Console.WriteLine($"  Version: {plan.Runtime.Kind} {plan.Runtime.Version} ({plan.Runtime.Architecture})");
    Console.WriteLine($"  Remove:  {plan.Runtime.InstallRoot}");
    Console.WriteLine($"  Trash:   {plan.TrashPath}");
    if (plan.References.Count == 0)
    {
        Console.WriteLine("  References: none");
    }
    else
    {
        Console.WriteLine("  References:");
        foreach (RuntimeReference reference in plan.References)
        {
            Console.WriteLine($"    {reference.Kind}: {reference.Owner} · {reference.Detail}");
        }
    }

    bool force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
    if (plan.IsReferenced && !force)
    {
        Console.Error.WriteLine(
            "Uninstall is blocked by references. Remove them first, or review and add --force.");
        return 2;
    }

    if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(force
            ? "Preview only. --force permits breaking references; add --yes to execute."
            : "Preview only. Add --yes to execute.");
        return 0;
    }

    ManagedRuntimeUninstallResult result = await uninstaller.ExecuteAsync(
        plan,
        force,
        cancellationToken);
    if (!result.Success)
    {
        Console.Error.WriteLine(result.Error);
        return 2;
    }

    Console.WriteLine(result.PendingTrashCleanup
        ? "Registry entry removed; files remain in .trash for later cleanup."
        : "Runtime uninstalled. Shared package downloads were retained.");
    return 0;
}

static async Task<int> RunUseAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length < 3
        || !TryParseRuntimeKind(args[0], out RuntimeKind kind)
        || !VersionSelector.TryParse(args[1], out VersionSelector? selector)
        || !args.Contains("--global", StringComparer.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus use <runtime> <selector> --global [--root directory]");
        return 1;
    }

    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    RegistryLoadResult registry = await new ManagedRuntimeRegistry(managedRoot!).LoadAsync(cancellationToken);
    if (registry.Errors.Count > 0)
    {
        foreach (string error in registry.Errors)
        {
            Console.Error.WriteLine(error);
        }

        return 2;
    }

    RuntimeProfile requested = new(new Dictionary<RuntimeKind, VersionSelector> { [kind] = selector! });
    RuntimeResolutionResult resolution = new RuntimeResolver().Resolve(
        kind,
        new RuntimeResolutionContext(Global: requested),
        registry.Entries.Select(entry => entry.ToRuntimeInstallation()));
    if (!resolution.Success)
    {
        Console.Error.WriteLine(resolution.Error);
        return 2;
    }

    ManagedRuntimeEntry entry = registry.Entries.Single(candidate =>
        candidate.Id.Equals(resolution.Installation!.Id, StringComparison.OrdinalIgnoreCase));
    if (!File.Exists(entry.ExecutablePath))
    {
        Console.Error.WriteLine($"The selected runtime is missing: {entry.ExecutablePath}");
        return 2;
    }

    await new GlobalRuntimeProfileStore(managedRoot!).SetAsync(kind, selector!, cancellationToken);
    Console.WriteLine($"Global {kind} selection: {selector} -> {entry.Version}");
    Console.WriteLine($"Executable: {entry.ExecutablePath}");
    return 0;
}

static async Task<int> RunWhichAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || !TryParseRuntimeKind(args[0], out RuntimeKind kind))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus which <runtime> [--project directory] [--root directory]");
        return 1;
    }

    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    if (!TryGetOption(args, "--project", out string? projectPath, out string? projectError))
    {
        Console.Error.WriteLine(projectError);
        return 1;
    }

    projectPath ??= Directory.GetCurrentDirectory();
    if (!TryCreateSessionProfile(kind, out RuntimeProfile? session, out string? sessionError))
    {
        Console.Error.WriteLine(sessionError);
        return 2;
    }

    ManagedRuntimeResolutionResult result = await new ManagedRuntimeResolutionService(managedRoot!).ResolveAsync(
        kind,
        projectPath,
        session,
        cancellationToken: cancellationToken);
    if (!result.Success)
    {
        foreach (string error in result.Errors)
        {
            Console.Error.WriteLine(error);
        }

        return 2;
    }

    Console.WriteLine($"Runtime:    {result.Entry!.Kind} {result.Entry.Version} ({result.Entry.Architecture})");
    Console.WriteLine($"Scope:      {result.Resolution!.Scope}");
    Console.WriteLine($"Selector:   {result.Resolution.Selector}");
    Console.WriteLine($"Executable: {result.Entry.ExecutablePath}");
    return 0;
}

static async Task<int> RunExecAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || !TryParseRuntimeKind(args[0], out RuntimeKind kind))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus exec <runtime> [--project directory] [--root directory] -- [arguments]");
        return 1;
    }

    int separator = Array.IndexOf(args, "--");
    string[] options = separator >= 0 ? args[..separator] : args;
    string[] childArguments = separator >= 0 ? args[(separator + 1)..] : [];
    if (!TryGetManagedRoot(options, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    if (!TryGetOption(options, "--project", out string? projectPath, out string? projectError))
    {
        Console.Error.WriteLine(projectError);
        return 1;
    }

    projectPath ??= Directory.GetCurrentDirectory();
    if (!Directory.Exists(projectPath))
    {
        Console.Error.WriteLine($"Project directory does not exist: {Path.GetFullPath(projectPath)}");
        return 1;
    }

    if (!TryCreateSessionProfile(kind, out RuntimeProfile? session, out string? sessionError))
    {
        Console.Error.WriteLine(sessionError);
        return 2;
    }

    ManagedRuntimeResolutionResult resolved = await new ManagedRuntimeResolutionService(managedRoot!).ResolveAsync(
        kind,
        projectPath,
        session,
        cancellationToken: cancellationToken);
    if (!resolved.Success)
    {
        foreach (string error in resolved.Errors)
        {
            Console.Error.WriteLine(error);
        }

        return 2;
    }

    ManagedRuntimeEntry entry = resolved.Entry!;
    ProcessStartInfo startInfo = new()
    {
        FileName = entry.ExecutablePath,
        WorkingDirectory = Path.GetFullPath(projectPath),
        UseShellExecute = false,
    };
    foreach (string argument in childArguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    PrependRuntimePath(startInfo, entry);
    if (entry.Kind == RuntimeKind.Java)
    {
        startInfo.Environment["JAVA_HOME"] = entry.InstallRoot;
    }

    using Process process = new() { StartInfo = startInfo };
    if (!process.Start())
    {
        Console.Error.WriteLine("The selected runtime process could not be started.");
        return 2;
    }

    try
    {
        await process.WaitForExitAsync(cancellationToken);
    }
    catch (OperationCanceledException)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        throw;
    }

    return process.ExitCode;
}

static async Task<int> RunToolAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0
        || !ManagedToolCommandResolver.TryGetRuntimeKind(args[0], out RuntimeKind runtimeKind))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus tool <pip|pip3|npm|npx|javac|jar> [--project directory] [--root directory] -- [arguments]");
        return 1;
    }

    int separator = Array.IndexOf(args, "--");
    string[] options = separator >= 0 ? args[..separator] : args;
    string[] childArguments = separator >= 0 ? args[(separator + 1)..] : [];
    if (!TryGetManagedRoot(options, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    if (!TryGetOption(options, "--project", out string? projectPath, out string? projectError))
    {
        Console.Error.WriteLine(projectError);
        return 1;
    }

    projectPath ??= Directory.GetCurrentDirectory();
    if (!Directory.Exists(projectPath))
    {
        Console.Error.WriteLine($"Project directory does not exist: {Path.GetFullPath(projectPath)}");
        return 1;
    }

    if (!TryCreateSessionProfile(runtimeKind, out RuntimeProfile? session, out string? sessionError))
    {
        Console.Error.WriteLine(sessionError);
        return 2;
    }

    ManagedToolCommandResult resolved = await new ManagedToolCommandResolver(managedRoot!).ResolveAsync(
        args[0],
        projectPath,
        session,
        cancellationToken);
    if (!resolved.Success)
    {
        foreach (string error in resolved.Errors)
        {
            Console.Error.WriteLine(error);
        }

        return 2;
    }

    ManagedToolCommand command = resolved.Command!;
    ProcessStartInfo startInfo = new()
    {
        FileName = command.ExecutablePath,
        WorkingDirectory = Path.GetFullPath(projectPath),
        UseShellExecute = false,
    };
    foreach (string argument in command.PrefixArguments.Concat(childArguments))
    {
        startInfo.ArgumentList.Add(argument);
    }

    PrependRuntimePath(startInfo, command.Runtime);
    if (command.RuntimeKind == RuntimeKind.Java)
    {
        startInfo.Environment["JAVA_HOME"] = command.Runtime.InstallRoot;
    }

    using Process process = new() { StartInfo = startInfo };
    if (!process.Start())
    {
        Console.Error.WriteLine("The selected managed tool could not be started.");
        return 2;
    }

    try
    {
        await process.WaitForExitAsync(cancellationToken);
    }
    catch (OperationCanceledException)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        throw;
    }

    return process.ExitCode;
}

static async Task<int> RunShimAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("Usage: autoenvplus shim <install|rollback> [options]");
        return 1;
    }

    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    UserPathManager pathManager = new(managedRoot!, new WindowsUserEnvironmentVariableStore());
    if (args[0].Equals("rollback", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: autoenvplus shim rollback <snapshot-file> [--root directory]");
            return 1;
        }

        UserPathMutationResult rollback = await pathManager.RollbackAsync(args[1], cancellationToken);
        if (!rollback.Success)
        {
            Console.Error.WriteLine(rollback.Error);
            return 2;
        }

        Console.WriteLine("User PATH restored from snapshot.");
        return 0;
    }

    if (!args[0].Equals("install", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Usage: autoenvplus shim <install|rollback> [options]");
        return 1;
    }

    string shimDirectory = Path.Combine(managedRoot!, "shims");
    UserPathMutationPlan pathPlan = pathManager.PlanEnsureFirst(shimDirectory);
    string? nativeShim = FindBundledNativeShim();
    Console.WriteLine("Shim activation plan");
    Console.WriteLine($"  Directory: {shimDirectory}");
    Console.WriteLine($"  Engine:    {(nativeShim is null ? "CMD fallback" : "native Win32 x64")}");
    Console.WriteLine("  Commands:  python, python3, pip, pip3, node, npm, npx, java, javac, jar");
    Console.WriteLine($"  PATH:      {(pathPlan.Changed ? "add/move Shim directory to first user position" : "already active")}");
    if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine("Preview only: no files or environment variables were changed. Add --yes to apply.");
        return 0;
    }

    GetCurrentCliInvocation(out string executable, out string[] prefixArguments);
    CommandShimInstallResult shims = await new CommandShimManager().InstallAsync(
        managedRoot!,
        executable,
        prefixArguments,
        nativeShim,
        cancellationToken);
    UserPathMutationResult applied = await pathManager.ApplyAsync(
        pathManager.PlanEnsureFirst(shims.ShimDirectory),
        cancellationToken);
    if (!applied.Success)
    {
        Console.Error.WriteLine(applied.Error);
        return 2;
    }

    Console.WriteLine(
        $"Installed {shims.ShimFiles.Count} {shims.Implementation} command Shims in {shims.ShimDirectory}");
    Console.WriteLine(applied.Changed
        ? $"User PATH updated. Snapshot: {applied.SnapshotPath}"
        : "User PATH already contained the Shim directory first.");
    Console.WriteLine("Open a new terminal before using python/node/java directly.");
    return 0;
}

static async Task<int> RunShellAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0
        || !args[0].Equals("powershell", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus shell powershell [--profile file] [--root directory] [--install-profile --yes] [--rollback snapshot --yes]");
        return 1;
    }

    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    if (!TryGetOption(args, "--profile", out string? profilePath, out string? profileError))
    {
        Console.Error.WriteLine(profileError);
        return 1;
    }

    if (!TryGetOption(args, "--rollback", out string? snapshotPath, out string? rollbackError))
    {
        Console.Error.WriteLine(rollbackError);
        return 1;
    }

    GetCurrentCliInvocation(out string executable, out string[] prefixArguments);
    PowerShellIntegrationManager manager = new(
        managedRoot!,
        executable,
        prefixArguments);
    if (snapshotPath is not null)
    {
        Console.WriteLine("PowerShell Profile rollback plan");
        Console.WriteLine($"  Snapshot: {Path.GetFullPath(snapshotPath)}");
        if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Preview only: no Profile was changed. Add --yes to restore this snapshot.");
            return 0;
        }

        PowerShellIntegrationResult rollback = await manager.RollbackAsync(
            snapshotPath,
            cancellationToken);
        if (!rollback.Success)
        {
            Console.Error.WriteLine(rollback.Error);
            return 2;
        }

        Console.WriteLine("PowerShell Profile restored from snapshot.");
        return 0;
    }

    profilePath ??= PowerShellIntegrationManager.GetDefaultWindowsPowerShellProfilePath();
    PowerShellIntegrationPlan plan;
    try
    {
        plan = manager.PlanInstall(profilePath);
    }
    catch (Exception exception) when (exception is IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or ArgumentException
        or NotSupportedException)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }

    Console.WriteLine("PowerShell session integration plan");
    Console.WriteLine($"  Profile:      {plan.ProfilePath}");
    Console.WriteLine($"  Module:       {plan.ModulePath}");
    Console.WriteLine($"  Shim folder:  {Path.Combine(managedRoot!, "shims")}");
    Console.WriteLine("  Commands:     Use-AutoEnvPlusRuntime, Clear-AutoEnvPlusRuntime");
    Console.WriteLine("  Variables:    AUTOENVPLUS_PYTHON_VERSION, AUTOENVPLUS_NODE_VERSION, AUTOENVPLUS_JAVA_VERSION");
    Console.WriteLine($"  Profile:      {(plan.ProfileChanged ? "create/update with snapshot" : "managed block already current")}");
    Console.WriteLine($"  Module:       {(plan.ModuleChanged ? "create/update atomically" : "already current")}");
    if (plan.ExistingProfileBlockCount > 1)
    {
        Console.WriteLine(
            $"  Cleanup:      replace {plan.ExistingProfileBlockCount} existing managed blocks with one current block");
    }

    if (args.Contains("--show-module", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine();
        Console.WriteLine("Generated AutoEnvPlus.PowerShell.psm1");
        Console.WriteLine(plan.ModuleContent);
    }

    if (!args.Contains("--install-profile", StringComparer.OrdinalIgnoreCase)
        || !args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(
            "Preview only: no files were changed. Add --install-profile --yes to apply this exact plan.");
        Console.WriteLine("Use --show-module to review the complete generated module.");
        return 0;
    }

    CommandShimInstallResult shims = await new CommandShimManager().InstallAsync(
        managedRoot!,
        executable,
        prefixArguments,
        FindBundledNativeShim(),
        cancellationToken);
    PowerShellIntegrationResult applied = await manager.ApplyAsync(plan, cancellationToken);
    if (!applied.Success)
    {
        Console.Error.WriteLine(applied.Error);
        return 2;
    }

    Console.WriteLine(
        $"Installed {shims.ShimFiles.Count} {shims.Implementation} session Shims in {shims.ShimDirectory}");
    Console.WriteLine($"PowerShell module installed: {applied.ModulePath}");
    Console.WriteLine(applied.SnapshotPath is null
        ? "PowerShell Profile integration was already current."
        : $"PowerShell Profile updated. Snapshot: {applied.SnapshotPath}");
    Console.WriteLine("Open a new PowerShell session, then run: Use-AutoEnvPlusRuntime python 3.13");
    return 0;
}

static async Task<int> RunStorageAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0
        || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
    {
        IReadOnlyList<CacheDirectoryLocation> locations = new CacheDirectoryService()
            .DiscoverCurrent();
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(
                locations.Select(location => new
                {
                    Id = location.Definition.Id,
                    Name = location.Definition.DisplayName,
                    location.DirectoryPath,
                    location.ConfigurationSource,
                    location.Exists,
                    location.Definition.SupportsMigration,
                    location.ConfigurationFilePath,
                    location.Warning,
                }),
                CreateJsonOptions()));
            return locations.Any(location => location.Warning is not null) ? 2 : 0;
        }

        Console.WriteLine("AutoEnvPlus storage locations");
        foreach (CacheDirectoryLocation item in locations)
        {
            string state = item.Warning is not null
                ? "warning"
                : item.Exists ? "exists" : "missing";
            string migration = item.Definition.SupportsMigration ? "migratable" : "read-only";
            Console.WriteLine(
                $"  {item.Definition.Id,-8} [{state}, {migration}] {item.DirectoryPath}");
            Console.WriteLine($"    Source: {item.ConfigurationSource}");
            if (item.Warning is not null)
            {
                Console.WriteLine($"    Warning: {item.Warning}");
            }
        }

        return locations.Any(location => location.Warning is not null) ? 2 : 0;
    }

    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    CacheMigrationService migrationService = new(managedRoot);
    WindowsUserEnvironmentVariableStore environmentStore = new();
    if (args[0].Equals("rollback", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "Usage: autoenvplus storage rollback <snapshot-file> [--root directory] [--yes]");
            return 1;
        }

        string snapshot = Path.GetFullPath(args[1]);
        Console.WriteLine("Storage configuration rollback plan");
        Console.WriteLine($"  Snapshot: {snapshot}");
        Console.WriteLine("  Cache copy: retained; only the tool configuration is restored");
        if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Preview only: no configuration was changed. Add --yes to restore this snapshot.");
            return 0;
        }

        CacheMigrationResult rollback = await migrationService.RollbackAsync(
            snapshot,
            environmentStore,
            cancellationToken);
        if (!rollback.Success)
        {
            Console.Error.WriteLine(rollback.Error);
            return 2;
        }

        Console.WriteLine("Storage configuration restored from snapshot.");
        Console.WriteLine($"The migrated copy remains at: {rollback.DestinationPath}");
        return 0;
    }

    if (!args[0].Equals("migrate", StringComparison.OrdinalIgnoreCase)
        || args.Length < 3)
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus storage <list|migrate|rollback> [options]");
        Console.Error.WriteLine(
            "       autoenvplus storage migrate <pip|npm|pnpm|yarn|nuget|nuget-http|nuget-plugins|maven|gradle|vcpkg|conan> <destination> [--root directory] [--yes]");
        return 1;
    }

    CacheDirectoryLocation? location = new CacheDirectoryService()
        .DiscoverCurrent()
        .FirstOrDefault(candidate => candidate.Definition.Id.Equals(
            args[1],
            StringComparison.OrdinalIgnoreCase));
    if (location is null)
    {
        Console.Error.WriteLine($"Unknown storage id: {args[1]}");
        return 1;
    }

    CacheMigrationPlan plan;
    try
    {
        plan = migrationService.CreatePlan(location, args[2]);
    }
    catch (Exception exception) when (exception is IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or InvalidOperationException
        or NotSupportedException
        or ArgumentException)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }

    Console.WriteLine($"{location.Definition.DisplayName} storage migration plan");
    Console.WriteLine($"  Source:        {plan.Source.DirectoryPath}");
    Console.WriteLine($"  Destination:   {plan.DestinationPath}");
    Console.WriteLine($"  Configuration: {plan.ConfigurationDescription}");
    Console.WriteLine("  Copy:          SHA-256 verify every file; retain source");
    Console.WriteLine("  Commit:        switch configuration only after copy verification");
    Console.WriteLine("  Rollback:      snapshot configuration before switching");
    if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine("Preview only: no files or configuration were changed. Add --yes to execute this exact plan.");
        return 0;
    }

    Progress<CacheMigrationProgress> progress = new(value =>
    {
        if (value.Stage.Equals("copy", StringComparison.Ordinal)
            && value.TotalBytes > 0)
        {
            Console.WriteLine(
                $"  [copy] {value.CompletedBytes}/{value.TotalBytes} bytes {value.RelativePath}");
            return;
        }

        Console.WriteLine($"  [{value.Stage}]");
    });
    CacheMigrationResult result = await migrationService.MigrateAsync(
        plan,
        environmentStore,
        progress,
        cancellationToken);
    if (!result.Success)
    {
        Console.Error.WriteLine(result.Error);
        return 2;
    }

    Console.WriteLine($"Storage migrated to: {result.DestinationPath}");
    Console.WriteLine("The source directory was retained for manual cleanup after validation.");
    Console.WriteLine(result.SnapshotPath is null
        ? "No rollback snapshot was requested for this operation."
        : $"Configuration snapshot: {result.SnapshotPath}");
    return 0;
}

static async Task<int> RunToolchainAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length > 0 && args[0].Equals("activate", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 2 || !args[1].Equals("msvc", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                "Usage: autoenvplus toolchain activate msvc [--instance id] [--host x64|x86] [--target x64|x86|arm64] [--yes]");
            return 1;
        }

        if (!TryGetOption(args, "--instance", out string? instanceId, out string? instanceError))
        {
            Console.Error.WriteLine(instanceError);
            return 1;
        }

        if (!TryGetOption(args, "--host", out string? hostValue, out string? hostError))
        {
            Console.Error.WriteLine(hostError);
            return 1;
        }

        if (!TryGetOption(args, "--target", out string? targetValue, out string? targetError))
        {
            Console.Error.WriteLine(targetError);
            return 1;
        }

        if (!TryParseCppArchitecture(hostValue ?? "x64", allowArm64: false, out RuntimeArchitecture host)
            || !TryParseCppArchitecture(targetValue ?? "x64", allowArm64: true, out RuntimeArchitecture target))
        {
            Console.Error.WriteLine("--host must be x64 or x86; --target must be x64, x86, or arm64.");
            return 1;
        }

        CppToolchainDiscoveryService discovery = new();
        CppToolchainDiscoveryResult discovered = await discovery.DiscoverAsync(cancellationToken);
        VisualCppInstallation[] candidates = discovered.VisualStudioInstallations
            .Where(installation => instanceId is null
                || installation.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
        {
            Console.Error.WriteLine(instanceId is null
                ? "No Visual Studio installation with MSVC was found."
                : $"Visual Studio instance '{instanceId}' was not found.");
            return 2;
        }

        if (candidates.Length > 1)
        {
            Console.Error.WriteLine("More than one Visual Studio installation was found; choose --instance:");
            foreach (VisualCppInstallation candidate in candidates)
            {
                Console.Error.WriteLine($"  {candidate.InstanceId}: {candidate.DisplayName}");
            }

            return 1;
        }

        CppActivationPlan plan;
        try
        {
            plan = discovery.CreateActivationPlan(candidates[0], target, host);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or NotSupportedException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        Console.WriteLine("MSVC developer terminal activation plan");
        Console.WriteLine($"  Instance: {candidates[0].InstanceId} · {candidates[0].DisplayName}");
        Console.WriteLine($"  Host:     {plan.HostArchitecture}");
        Console.WriteLine($"  Target:   {plan.TargetArchitecture}");
        Console.WriteLine($"  Command:  {plan.Executable}");
        Console.WriteLine($"  Arguments:{string.Concat(plan.Arguments.Select(argument => $" [{argument}]"))}");
        if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Preview only: no terminal was started. Add --yes to open this exact environment.");
            return 0;
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = plan.Executable,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = true,
        };
        foreach (string argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo);
        Console.WriteLine("MSVC developer terminal started.");
        return 0;
    }

    if (args.Length > 0 && args[0].Equals("install", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 2 || !TryParseToolchainComponent(args[1], out ToolchainComponent component))
        {
            Console.Error.WriteLine(
                "Usage: autoenvplus toolchain install <msvc|llvm|mingw|cmake|ninja> [--yes]");
            return 1;
        }

        WingetToolchainInstaller installer = new();
        string? winget = installer.FindWinget();
        if (winget is null)
        {
            Console.Error.WriteLine("WinGet was not found on PATH.");
            return 2;
        }

        ExternalToolInstallPlan plan = installer.CreatePlan(component, winget);
        Console.WriteLine("Toolchain component install plan");
        Console.WriteLine($"  Component: {plan.DisplayName}");
        Console.WriteLine($"  Package:   {plan.PackageId}");
        Console.WriteLine($"  Provider:  {plan.Executable}");
        Console.WriteLine(
            $"  Elevation: {(plan.MayRequireElevation ? "may be requested" : "normally not required")}");
        if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Preview only. Add --yes to invoke this allowlisted WinGet plan.");
            return 0;
        }

        ExternalToolInstallResult installed = await installer.InstallAsync(plan, cancellationToken);
        if (!installed.Success)
        {
            Console.Error.WriteLine($"WinGet exited with {installed.ExitCode}.");
            Console.Error.WriteLine(installed.StandardError);
            return 2;
        }

        Console.WriteLine(installed.StandardOutput);
        return 0;
    }

    if (args.Length == 0 || !args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Usage: autoenvplus toolchain <list|install|activate> [options]");
        return 1;
    }

    CppToolchainDiscoveryResult result = await new CppToolchainDiscoveryService().DiscoverAsync(
        cancellationToken);
    if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(JsonSerializer.Serialize(result, CreateJsonOptions()));
        return 0;
    }

    Console.WriteLine("Visual C++ installations");
    foreach (VisualCppInstallation installation in result.VisualStudioInstallations)
    {
        Console.WriteLine($"  {installation.DisplayName}");
        Console.WriteLine($"    MSVC: {installation.MsvcToolsVersion ?? "not detected"}");
        Console.WriteLine($"    Root: {installation.InstallationPath}");
        foreach (CppArchitecturePair pair in installation.AvailableArchitecturePairs ?? [])
        {
            Console.WriteLine(
                $"    Pair: {pair.HostArchitecture} -> {pair.TargetArchitecture} ({pair.VcVarsArgument})");
        }
    }

    if (result.VisualStudioInstallations.Count == 0)
    {
        Console.WriteLine("  None detected.");
    }

    Console.WriteLine("Windows SDKs");
    foreach (WindowsSdkInstallation sdk in result.WindowsSdks)
    {
        Console.WriteLine($"  {sdk.Version} ({string.Join(", ", sdk.Architectures)})");
    }

    if (result.WindowsSdks.Count == 0)
    {
        Console.WriteLine("  None detected.");
    }

    Console.WriteLine("Build tools on PATH");
    foreach (DiscoveredRuntime tool in result.BuildTools)
    {
        Console.WriteLine(
            $"  {tool.Kind,-8} {tool.Version?.ToString() ?? "unknown",-16} {tool.ExecutablePath}");
    }

    if (result.BuildTools.Count == 0)
    {
        Console.WriteLine("  None detected.");
    }

    foreach (string error in result.Errors)
    {
        Console.Error.WriteLine($"  warning: {error}");
    }

    return 0;
}

static bool TryParseCppArchitecture(
    string value,
    bool allowArm64,
    out RuntimeArchitecture architecture)
{
    architecture = value.ToLowerInvariant() switch
    {
        "x64" => RuntimeArchitecture.X64,
        "x86" => RuntimeArchitecture.X86,
        "arm64" when allowArm64 => RuntimeArchitecture.Arm64,
        _ => RuntimeArchitecture.Any,
    };
    return architecture != RuntimeArchitecture.Any;
}

static async Task<int> RunProjectAsync(string[] args, CancellationToken cancellationToken)
{
    string command = "status";
    string? startPath = null;
    if (args.Length > 0 && args[0] is "status" or "import" or "lock" or "terminal" or "cmake-preset")
    {
        command = args[0].ToLowerInvariant();
        if (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            startPath = args[1];
        }
    }
    else if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
    {
        startPath = args[0];
    }

    startPath ??= Directory.GetCurrentDirectory();
    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    if (command == "cmake-preset")
    {
        string projectRoot = Path.GetFullPath(startPath);
        if (!Directory.Exists(projectRoot)
            || !File.Exists(Path.Combine(projectRoot, "CMakeLists.txt")))
        {
            Console.Error.WriteLine(
                $"CMake project does not contain CMakeLists.txt: {projectRoot}");
            return 2;
        }

        CMakeUserPresetsService service = new(managedRoot!, projectRoot);
        if (!TryGetOption(args, "--rollback", out string? snapshotPath, out string? rollbackError))
        {
            Console.Error.WriteLine(rollbackError);
            return 1;
        }

        if (snapshotPath is not null)
        {
            Console.WriteLine("CMake user presets rollback plan");
            Console.WriteLine($"  Project:  {projectRoot}");
            Console.WriteLine($"  Snapshot: {Path.GetFullPath(snapshotPath)}");
            if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("Preview only: no project file was changed. Add --yes to restore this snapshot.");
                return 0;
            }

            CMakeUserPresetsResult rollback = await service.RollbackAsync(
                snapshotPath,
                cancellationToken);
            if (!rollback.Success)
            {
                Console.Error.WriteLine(rollback.Error);
                return 2;
            }

            Console.WriteLine($"Restored: {rollback.PresetsPath}");
            return 0;
        }

        if (!TryGetOption(args, "--instance", out string? instanceId, out string? instanceError))
        {
            Console.Error.WriteLine(instanceError);
            return 1;
        }

        if (!TryGetOption(args, "--host", out string? hostValue, out string? hostError))
        {
            Console.Error.WriteLine(hostError);
            return 1;
        }

        if (!TryGetOption(args, "--target", out string? targetValue, out string? targetError))
        {
            Console.Error.WriteLine(targetError);
            return 1;
        }

        if (!TryParseCppArchitecture(hostValue ?? "x64", allowArm64: false, out RuntimeArchitecture host)
            || !TryParseCppArchitecture(targetValue ?? "x64", allowArm64: true, out RuntimeArchitecture target))
        {
            Console.Error.WriteLine("--host must be x64 or x86; --target must be x64, x86, or arm64.");
            return 1;
        }

        CppToolchainDiscoveryResult discovered = await new CppToolchainDiscoveryService()
            .DiscoverAsync(cancellationToken);
        VisualCppInstallation[] installations = discovered.VisualStudioInstallations
            .Where(installation => instanceId is null
                || installation.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (installations.Length != 1)
        {
            Console.Error.WriteLine(installations.Length == 0
                ? "No matching Visual Studio MSVC instance was found."
                : "More than one Visual Studio instance was found; choose --instance.");
            foreach (VisualCppInstallation installation in installations)
            {
                Console.Error.WriteLine($"  {installation.InstanceId}: {installation.DisplayName}");
            }

            return installations.Length == 0 ? 2 : 1;
        }

        CppArchitecturePair? pair = (installations[0].AvailableArchitecturePairs ?? [])
            .FirstOrDefault(candidate => candidate.HostArchitecture == host
                && candidate.TargetArchitecture == target);
        if (pair is null)
        {
            Console.Error.WriteLine(
                $"{installations[0].DisplayName} does not contain the {host} -> {target} compiler tools.");
            return 2;
        }

        CMakeUserPresetsPlan plan;
        try
        {
            plan = service.CreatePlan(installations[0], pair);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        Console.WriteLine("CMake user presets plan");
        Console.WriteLine($"  Project:   {plan.ProjectRoot}");
        Console.WriteLine($"  File:      {plan.PresetsPath}");
        Console.WriteLine($"  Configure: {plan.ConfigurePresetName}");
        Console.WriteLine($"  Build:     {plan.BuildPresetName}");
        Console.WriteLine($"  Instance:  {installations[0].InstanceId} · {installations[0].DisplayName}");
        Console.WriteLine($"  Pair:      {pair.HostArchitecture} -> {pair.TargetArchitecture}");
        Console.WriteLine();
        Console.WriteLine(plan.After);
        if (!args.Contains("--write", StringComparer.OrdinalIgnoreCase)
            || !args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Preview only: no project file was changed. Add --write --yes to apply this exact plan.");
            return 0;
        }

        CMakeUserPresetsResult applied = await service.ApplyAsync(plan, cancellationToken);
        if (!applied.Success)
        {
            Console.Error.WriteLine(applied.Error);
            return 2;
        }

        await new KnownProjectStore(managedRoot!).AddAsync(projectRoot, cancellationToken);
        Console.WriteLine($"Created or updated: {applied.PresetsPath}");
        Console.WriteLine($"Snapshot: {applied.SnapshotPath}");
        return 0;
    }

    if (command == "import")
    {
        ProjectEnvironmentImportService importer = new();
        ProjectEnvironmentImportResult import = importer.Discover(startPath);
        if (!import.Found)
        {
            Console.Error.WriteLine($"No supported project version files were found from {Path.GetFullPath(startPath)}.");
            return 2;
        }

        Console.WriteLine($"Project root: {import.ProjectRoot}");
        foreach (ImportedRuntimeSelection source in import.Sources)
        {
            Console.WriteLine(
                $"  {source.Kind,-8} {source.Selector,-12} from {source.SourcePath} ({source.RawValue})");
        }

        foreach (string warning in import.Warnings)
        {
            Console.Error.WriteLine($"  warning: {warning}");
        }

        if (!args.Contains("--write", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Preview only. Add --write to create autoenvplus.toml.");
            return 0;
        }

        string importedManifestPath = await importer.WriteManifestAsync(import, cancellationToken: cancellationToken);
        await new KnownProjectStore(managedRoot!).AddAsync(import.ProjectRoot!, cancellationToken);
        Console.WriteLine($"Created: {importedManifestPath}");
        return 0;
    }

    if (command == "terminal")
    {
        ProjectTerminalService terminal = new(managedRoot!);
        ProjectTerminalPlan plan;
        try
        {
            plan = await terminal.CreatePlanAsync(startPath, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        Console.WriteLine("Project terminal activation plan");
        Console.WriteLine($"  Project:  {plan.ProjectRoot}");
        Console.WriteLine($"  Manifest: {plan.ManifestPath}");
        Console.WriteLine($"  Shell:    {plan.ShellExecutable} {string.Join(' ', plan.ShellArguments)}");
        Console.WriteLine($"  Shims:    {plan.ShimDirectory}");
        foreach (ProjectTerminalSelection selection in plan.Selections)
        {
            Console.WriteLine(
                $"  {selection.Kind,-8} {selection.RequestedSelector,-10} -> {selection.ResolvedVersion} ({selection.RuntimeId})");
            Console.WriteLine($"    {selection.EnvironmentVariable}={selection.ResolvedVersion}");
        }

        foreach (string warning in plan.Warnings)
        {
            Console.Error.WriteLine($"  warning: {warning}");
        }

        foreach (string planError in plan.Errors)
        {
            Console.Error.WriteLine($"  error: {planError}");
        }

        if (!plan.CanLaunch)
        {
            return 2;
        }

        if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Preview only: no terminal was started. Add --yes to open this exact environment.");
            return 0;
        }

        try
        {
            int processId = await terminal.LaunchAsync(plan, cancellationToken);
            await new KnownProjectStore(managedRoot!).AddAsync(plan.ProjectRoot, cancellationToken);
            Console.WriteLine($"Project terminal started (PID {processId}).");
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    string? manifestPath = new ProjectManifestService().FindManifest(startPath);
    if (manifestPath is null)
    {
        Console.Error.WriteLine(
            $"No {ProjectManifestService.ManifestFileName} was found from '{Path.GetFullPath(startPath)}'.");
        return 2;
    }

    if (command == "lock")
    {
        RegistryLoadResult registry = await new ManagedRuntimeRegistry(managedRoot!).LoadAsync(cancellationToken);
        if (registry.Errors.Count > 0)
        {
            foreach (string error in registry.Errors)
            {
                Console.Error.WriteLine(error);
            }

            return 2;
        }

        ProjectLockResult locked = await new ProjectLockFileService().CreateAsync(
            manifestPath,
            registry.Entries,
            cancellationToken: cancellationToken);
        if (!locked.Success)
        {
            foreach (string error in locked.Errors)
            {
                Console.Error.WriteLine(error);
            }

            return 2;
        }

        await new KnownProjectStore(managedRoot!).AddAsync(
            Path.GetDirectoryName(manifestPath)!,
            cancellationToken);
        Console.WriteLine($"Lock file: {locked.LockPath}");
        foreach (ProjectLockEntry entry in locked.Document!.Runtimes)
        {
            Console.WriteLine(
                $"  {entry.Kind,-8} {entry.RequestedSelector,-10} -> {entry.ResolvedVersion} {entry.Architecture} {entry.ProviderId}");
        }

        return 0;
    }

    ProjectManifestLoadResult manifest = new ProjectManifestService().Load(manifestPath);
    Console.WriteLine($"Manifest: {manifest.Manifest.ManifestPath}");
    foreach ((RuntimeKind kind, VersionSelector selector) in manifest.Manifest.Tools)
    {
        Console.WriteLine($"  {kind,-8} {selector}");
    }

    foreach (ProjectManifestError error in manifest.Errors)
    {
        Console.Error.WriteLine($"  line {error.LineNumber}: {error.Message}");
    }

    string lockPath = Path.Combine(manifest.Manifest.ProjectRoot, ProjectLockFileService.LockFileName);
    if (File.Exists(lockPath))
    {
        ProjectLockResult locked = await new ProjectLockFileService().LoadAsync(lockPath, cancellationToken);
        bool current = locked.Success
            && await new ProjectLockFileService().IsCurrentAsync(
                locked.Document!,
                manifestPath,
                cancellationToken);
        Console.WriteLine($"Lock: {lockPath} ({(current ? "current" : "stale or invalid")})");
    }

    return manifest.Success ? 0 : 2;
}

static int RunResolve(string[] args)
{
    if (args.Length < 2 || !TryParseRuntimeKind(args[0], out RuntimeKind kind))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus resolve <python|node|java|dotnet> <selector> [installed-version ...]");
        return 1;
    }

    if (!VersionSelector.TryParse(args[1], out VersionSelector? selector))
    {
        Console.Error.WriteLine($"Unsupported selector: {args[1]}");
        return 1;
    }

    List<RuntimeInstallation> installations = [];
    foreach (string value in args.Skip(2))
    {
        if (!RuntimeVersion.TryParse(value, out RuntimeVersion? version))
        {
            Console.Error.WriteLine($"Unsupported installed version: {value}");
            return 1;
        }

        installations.Add(new RuntimeInstallation(
            $"{kind}-{version}",
            kind,
            version!,
            RuntimeArchitecture.X64,
            string.Empty,
            RuntimeOwnership.Managed,
            ["latest"]));
    }

    RuntimeProfile global = new(new Dictionary<RuntimeKind, VersionSelector> { [kind] = selector! });
    RuntimeResolutionResult result = new RuntimeResolver().Resolve(
        kind,
        new RuntimeResolutionContext(Global: global),
        installations);
    Console.WriteLine($"Selector: {result.Selector}; scope: {result.Scope}");
    if (!result.Success)
    {
        Console.Error.WriteLine(result.Error);
        return 2;
    }

    Console.WriteLine($"Resolved: {result.Installation!.Version} ({result.Installation.Architecture})");
    return 0;
}

static IRuntimeCatalogProvider? CreateProvider(
    string runtime,
    HttpClient client,
    RuntimeArchitecture architecture,
    string[] args,
    RuntimeVersion? requestedVersion,
    out string? error)
{
    error = null;
    if (runtime.Equals("python", StringComparison.OrdinalIgnoreCase))
    {
        return new PythonOrgCatalogProvider(client, architecture);
    }

    if (runtime.Equals("node", StringComparison.OrdinalIgnoreCase)
        || runtime.Equals("nodejs", StringComparison.OrdinalIgnoreCase))
    {
        return new NodeJsCatalogProvider(client);
    }

    if (runtime.Equals("java", StringComparison.OrdinalIgnoreCase))
    {
        int feature = requestedVersion?.Major ?? 21;
        if (TryFindOptionIndex(args, "--feature", out int featureIndex)
            && (featureIndex + 1 >= args.Length
                || !int.TryParse(args[featureIndex + 1], out feature)
                || feature < 8))
        {
            error = "--feature must be a Java feature version of 8 or newer.";
            return null;
        }

        return new AdoptiumCatalogProvider(client, feature, architecture);
    }

    error = "The runtime provider must be python, node, or java.";
    return null;
}

static HttpClient CreateHttpClient(TimeSpan timeout)
{
    HttpClient client = new() { Timeout = timeout };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoEnvPlus/0.1");
    return client;
}

static JsonSerializerOptions CreateJsonOptions() => new()
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() },
};

static bool TryParseRuntimeKind(string value, out RuntimeKind kind)
{
    string normalized = value.Equals("node", StringComparison.OrdinalIgnoreCase)
        ? nameof(RuntimeKind.NodeJs)
        : value;
    return Enum.TryParse(normalized, true, out kind);
}

static bool TryParseToolchainComponent(string value, out ToolchainComponent component)
{
    string normalized = value.Equals("msvc", StringComparison.OrdinalIgnoreCase)
        ? nameof(ToolchainComponent.MsvcBuildTools)
        : value;
    return Enum.TryParse(normalized, true, out component);
}

static bool TryParseArchitecture(
    string[] args,
    out RuntimeArchitecture architecture,
    out string? error)
{
    architecture = RuntimeArchitecture.X64;
    error = null;
    if (!TryFindOptionIndex(args, "--arch", out int index))
    {
        return true;
    }

    if (index + 1 >= args.Length
        || !Enum.TryParse(args[index + 1], true, out architecture)
        || architecture == RuntimeArchitecture.Any)
    {
        error = "--arch must be x64, x86, or arm64.";
        return false;
    }

    return true;
}

static bool TryGetManagedRoot(
    string[] args,
    out string? managedRoot,
    out string? error)
{
    if (!TryGetOption(args, "--root", out string? configuredRoot, out error))
    {
        managedRoot = null;
        return false;
    }

    managedRoot = Path.GetFullPath(configuredRoot ?? Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "AutoEnvPlus"));
    return true;
}

static bool TryGetOption(
    string[] args,
    string option,
    out string? value,
    out string? error)
{
    value = null;
    error = null;
    if (!TryFindOptionIndex(args, option, out int index))
    {
        return true;
    }

    if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
    {
        error = $"{option} requires a value.";
        return false;
    }

    value = args[index + 1];
    return true;
}

static bool TryFindOptionIndex(string[] args, string option, out int index)
{
    index = Array.FindIndex(
        args,
        candidate => candidate.Equals(option, StringComparison.OrdinalIgnoreCase));
    return index >= 0;
}

static bool TryCreateSessionProfile(
    RuntimeKind kind,
    out RuntimeProfile? profile,
    out string? error)
{
    string variableName = kind switch
    {
        RuntimeKind.NodeJs => "AUTOENVPLUS_NODE_VERSION",
        _ => $"AUTOENVPLUS_{kind.ToString().ToUpperInvariant()}_VERSION",
    };
    string? value = System.Environment.GetEnvironmentVariable(variableName);
    if (string.IsNullOrWhiteSpace(value))
    {
        profile = null;
        error = null;
        return true;
    }

    if (!VersionSelector.TryParse(value, out VersionSelector? selector))
    {
        profile = null;
        error = $"Environment variable {variableName} contains an invalid selector: {value}";
        return false;
    }

    profile = new RuntimeProfile(new Dictionary<RuntimeKind, VersionSelector> { [kind] = selector! });
    error = null;
    return true;
}

static void GetCurrentCliInvocation(
    out string executable,
    out string[] prefixArguments)
{
    executable = System.Environment.ProcessPath
        ?? throw new InvalidOperationException("AutoEnvPlus could not determine its CLI executable path.");
    if (Path.GetFileNameWithoutExtension(executable).Equals(
        "dotnet",
        StringComparison.OrdinalIgnoreCase))
    {
        string assemblyPath = Path.Combine(AppContext.BaseDirectory, "autoenvplus.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new InvalidOperationException(
                "AutoEnvPlus could not determine the framework-dependent CLI assembly path.");
        }

        prefixArguments = [assemblyPath];
        return;
    }

    prefixArguments = [];
}

static string? FindBundledNativeShim()
{
    string besideCli = Path.Combine(AppContext.BaseDirectory, "autoenvplus-shim.exe");
    return File.Exists(besideCli) ? besideCli : null;
}

static void PrependRuntimePath(ProcessStartInfo startInfo, ManagedRuntimeEntry entry)
{
    IEnumerable<string> directories = entry.Kind switch
    {
        RuntimeKind.Python => [entry.InstallRoot, Path.Combine(entry.InstallRoot, "Scripts")],
        RuntimeKind.Java => [Path.Combine(entry.InstallRoot, "bin")],
        _ => [entry.InstallRoot],
    };
    string prefix = string.Join(
        Path.PathSeparator,
        directories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase));
    string inheritedPath = startInfo.Environment.TryGetValue("PATH", out string? value)
        ? value ?? string.Empty
        : string.Empty;
    startInfo.Environment["PATH"] = string.IsNullOrEmpty(prefix)
        ? inheritedPath
        : prefix + Path.PathSeparator + inheritedPath;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    _ = ShowHelp();
    return 1;
}

static int ShowHelp()
{
    Console.WriteLine("AutoEnvPlus CLI");
    Console.WriteLine();
    Console.WriteLine("  autoenvplus doctor [--json]");
    Console.WriteLine("  autoenvplus list [--managed] [--json] [--root directory]");
    Console.WriteLine("  autoenvplus catalog <python|node|java> [catalog options]");
    Console.WriteLine("  autoenvplus install <python|node|java> <exact-version> [--arch value] [--root directory] [--yes]");
    Console.WriteLine("  autoenvplus uninstall <managed-runtime-id> [--root directory] [--force] [--yes]");
    Console.WriteLine("  autoenvplus use <runtime> <selector> --global [--root directory]");
    Console.WriteLine("  autoenvplus which <runtime> [--project directory] [--root directory]");
    Console.WriteLine("  autoenvplus exec <runtime> [--project directory] [--root directory] -- [arguments]");
    Console.WriteLine("  autoenvplus tool <pip|pip3|npm|npx|javac|jar> [options] -- [arguments]");
    Console.WriteLine("  autoenvplus shim install [--root directory] [--yes]");
    Console.WriteLine("  autoenvplus shim rollback <snapshot-file> [--root directory]");
    Console.WriteLine("  autoenvplus shell powershell [--profile file] [--install-profile --yes] [--root directory]");
    Console.WriteLine("  autoenvplus shell powershell --rollback <snapshot-file> [--root directory] [--yes]");
    Console.WriteLine("  autoenvplus storage list [--json]");
    Console.WriteLine("  autoenvplus storage migrate <pip|npm|pnpm|yarn|nuget|nuget-http|nuget-plugins|maven|gradle|vcpkg|conan> <destination> [--root directory] [--yes]");
    Console.WriteLine("  autoenvplus storage rollback <snapshot-file> [--root directory] [--yes]");
    Console.WriteLine("  autoenvplus toolchain list [--json]");
    Console.WriteLine("  autoenvplus toolchain install <msvc|llvm|mingw|cmake|ninja> [--yes]");
    Console.WriteLine("  autoenvplus toolchain activate msvc [--instance id] [--host x64|x86] [--target value] [--yes]");
    Console.WriteLine("  autoenvplus project [status] [path]");
    Console.WriteLine("  autoenvplus project import [path] [--write] [--root directory]");
    Console.WriteLine("  autoenvplus project lock [path] [--root directory]");
    Console.WriteLine("  autoenvplus project terminal [path] [--root directory] [--yes]");
    Console.WriteLine("  autoenvplus project cmake-preset <path> [--instance id] [--host x64|x86] [--target value] [--write --yes]");
    Console.WriteLine("  autoenvplus project cmake-preset <path> --rollback <snapshot-file> [--root directory] [--yes]");
    Console.WriteLine("  autoenvplus resolve <runtime> <selector> [installed-version ...]");
    return 0;
}

internal sealed class ConsoleInstallProgress : IProgress<InstallProgress>
{
    private string? _lastStage;

    public void Report(InstallProgress value)
    {
        if (value.Stage.Equals(_lastStage, StringComparison.Ordinal))
        {
            return;
        }

        _lastStage = value.Stage;
        Console.WriteLine($"  [{value.Stage}]");
    }
}
