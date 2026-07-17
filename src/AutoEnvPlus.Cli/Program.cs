using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Diagnostics;
using AutoEnvPlus.Core.Downloads;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Plugins;
using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Providers.DotNet;
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
catch (HttpRequestException exception)
{
    string status = exception.StatusCode is null
        ? "transport failure"
        : $"HTTP {(int)exception.StatusCode.Value}";
    Console.Error.WriteLine(
        $"Error: network request failed ({status}); URI query and credentials were not logged.");
    return 1;
}
catch (Exception exception) when (exception is IOException
    or InvalidDataException
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
        "network" => await RunNetworkAsync(commandArgs, cancellationToken),
        "download" => await RunDownloadAsync(commandArgs, cancellationToken),
        "provider" => await RunProviderAsync(commandArgs, pluginsOnly: false, cancellationToken),
        "plugin" or "plugins" => await RunProviderAsync(commandArgs, pluginsOnly: true, cancellationToken),
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
        || !TryParseCatalogRuntimeKind(args[0], out RuntimeKind catalogKind))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus catalog <python|node|java|dotnet|msvc|llvm|mingw|cmake|ninja> [--provider official-id|plugin:id] [--feature java-major] [--lts] [--arch x64|x86|arm64] [--limit count] [--asset version] [--root directory] [--json]");
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

    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    NetworkSettings? networkSettings = await LoadNetworkSettingsAsync(
        managedRoot!,
        cancellationToken);
    if (networkSettings is null
        || !TryResolveRuntimeNetworkSettings(
            networkSettings,
            args[0],
            out EffectiveNetworkSettings? effectiveNetworkSettings))
    {
        return 2;
    }

    EffectiveNetworkSettings catalogNetwork =
        ToolchainRuntimeProviderPolicy.RequiresExplicitPlugin(catalogKind)
            ? effectiveNetworkSettings! with { Mirror = null }
            : effectiveNetworkSettings!;
    using HttpClient client = NetworkHttpClientFactory.Create(
        catalogNetwork,
        TimeSpan.FromSeconds(30));
    CliProviderResolution providerResolution = await ResolveCatalogProviderAsync(
        args[0],
        client,
        architecture,
        args,
        null,
        catalogNetwork.Mirror,
        managedRoot!,
        cancellationToken);
    if (providerResolution.Selection is not CliProviderSelection selection)
    {
        Console.Error.WriteLine(providerResolution.Error);
        return 1;
    }

    IRuntimeCatalogProvider provider = selection.Provider;

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
            release.Version == requestedVersion);
        if (selected is null)
        {
            Console.Error.WriteLine(
                $"{provider.Kind} {requestedVersion} ({architecture}) is not present in the selected catalog.");
            return 2;
        }

        RuntimePackageAsset asset = await provider.GetAssetAsync(selected, cancellationToken);
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            object output = selection.IsThirdParty
                ? new
                {
                    provider = selection.Provider.Id,
                    thirdParty = true,
                    authenticityNotice = DeclarativeRuntimeCatalogProvider.AuthenticityNotice,
                    asset,
                }
                : asset;
            Console.WriteLine(JsonSerializer.Serialize(output, CreateJsonOptions()));
        }
        else
        {
            Console.WriteLine($"Asset:   {asset.FileName}");
            Console.WriteLine($"URL:     {FormatProviderUri(asset.DownloadUri, selection.IsThirdParty)}");
            Console.WriteLine($"{asset.HashAlgorithm.DisplayName()}: {asset.PackageHash}");
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

            if (selection.IsThirdParty)
            {
                Console.WriteLine($"Trust:   {DeclarativeRuntimeCatalogProvider.AuthenticityNotice}");
            }
        }

        return 0;
    }

    RuntimeRelease[] releases = query.Take(limit).ToArray();
    if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
    {
        object output = selection.IsThirdParty
            ? new
            {
                provider = selection.Provider.Id,
                thirdParty = true,
                authenticityNotice = DeclarativeRuntimeCatalogProvider.AuthenticityNotice,
                releases,
            }
            : releases;
        Console.WriteLine(JsonSerializer.Serialize(output, CreateJsonOptions()));
        return 0;
    }

    Console.WriteLine($"{provider.Id} releases ({architecture})");
    if (selection.IsThirdParty)
    {
        Console.WriteLine($"  Trust: {DeclarativeRuntimeCatalogProvider.AuthenticityNotice}");
    }

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
        || !TryParseCatalogRuntimeKind(args[0], out RuntimeKind catalogKind)
        || !RuntimeVersion.TryParse(args[1], out RuntimeVersion? requestedVersion))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus install <python|node|java|dotnet|msvc|llvm|mingw|cmake|ninja> <exact-version> [--provider official-id|plugin:id] [--arch x64|x86|arm64] [--root directory] [--yes]");
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

    NetworkSettings? networkSettings = await LoadNetworkSettingsAsync(
        managedRoot!,
        cancellationToken);
    if (networkSettings is null
        || !TryResolveRuntimeNetworkSettings(
            networkSettings,
            args[0],
            out EffectiveNetworkSettings? effectiveNetworkSettings))
    {
        return 2;
    }

    EffectiveNetworkSettings installNetwork =
        ToolchainRuntimeProviderPolicy.RequiresExplicitPlugin(catalogKind)
            ? effectiveNetworkSettings! with { Mirror = null }
            : effectiveNetworkSettings!;
    using HttpClient client = NetworkHttpClientFactory.Create(
        installNetwork,
        TimeSpan.FromMinutes(10));
    CliProviderResolution providerResolution = await ResolveCatalogProviderAsync(
        args[0],
        client,
        architecture,
        args,
        requestedVersion,
        installNetwork.Mirror,
        managedRoot!,
        cancellationToken);
    if (providerResolution.Selection is not CliProviderSelection selection
        || selection.Provider is not IArchiveRuntimeProvider provider)
    {
        Console.Error.WriteLine(
            providerResolution.Error ?? "No archive provider is available.");
        return 1;
    }

    IReadOnlyList<RuntimeRelease> catalog = await provider.GetReleasesAsync(cancellationToken);
    RuntimeRelease? release = catalog.FirstOrDefault(item =>
        item.Architecture == architecture
        && item.Version == requestedVersion);
    if (release is null)
    {
        Console.Error.WriteLine(
            $"{provider.Kind} {requestedVersion} ({architecture}) is not available from {provider.Id}.");
        return 2;
    }

    RuntimePackageAsset asset = await provider.GetAssetAsync(release, cancellationToken);
    ArchiveInstallPlan plan;
    try
    {
        plan = provider.CreateInstallPlan(asset, managedRoot!);
    }
    catch (Exception exception) when (exception is ArgumentException
        or RuntimeProviderPluginException)
    {
        Console.Error.WriteLine($"The install plan is unsafe or invalid: {exception.Message}");
        return 2;
    }

    Console.WriteLine("Install plan");
    Console.WriteLine($"  Runtime:     {release.Kind} {release.Version} ({release.Architecture})");
    Console.WriteLine($"  Provider:    {release.ProviderId}");
    Console.WriteLine($"  Download:    {FormatProviderUri(asset.DownloadUri, selection.IsThirdParty)}");
    Console.WriteLine($"  {asset.HashAlgorithm.DisplayName()}:     {asset.PackageHash}");
    foreach (PackageVerification verification in asset.Verifications)
    {
        string evidenceLabel = selection.IsThirdParty
            ? "Declared reference"
            : "Verified by";
        Console.WriteLine(
            $"  {evidenceLabel}: {verification.Kind} {verification.Algorithm} from {verification.SourceUri}");
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

    if (selection.IsThirdParty)
    {
        Console.WriteLine(
            $"  Trust:       {DeclarativeRuntimeCatalogProvider.AuthenticityNotice}");
    }

    Console.WriteLine($"  Destination: {plan.DestinationRoot}");
    if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine("Preview only: no files were changed. Add --yes to execute this exact plan.");
        return 0;
    }

    string managedRuntimeId = provider is DeclarativeRuntimeCatalogProvider declarativeProvider
        ? declarativeProvider.CreateManagedRuntimeId(release)
        : $"{release.Kind.ToString().ToLowerInvariant()}-{release.Version}-{release.Architecture.ToString().ToLowerInvariant()}";
    ManagedRuntimeEntry entry = new(
        managedRuntimeId,
        release.ProviderId,
        release.Kind,
        release.Version,
        release.Architecture,
        plan.DestinationRoot,
        plan.ExpectedExecutableRelativePath,
        asset.PackageHash,
        DateTimeOffset.UtcNow,
        release.Channels,
        asset.HashAlgorithm);
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
    if (args.Length < 2
        || string.IsNullOrWhiteSpace(args[1])
        || !TryParseUseRuntimeKind(args[0], out RuntimeKind kind)
        || !VersionSelector.TryParse(args[1], out VersionSelector? selector))
    {
        PrintUseUsage(Console.Error);
        return 1;
    }

    if (!TryParseUseOptions(
            args,
            out string? runtimeId,
            out string? providerId,
            out string? optionError))
    {
        Console.Error.WriteLine(optionError);
        PrintUseUsage(Console.Error);
        return 1;
    }

    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    ManagedRuntimeEntry? expectedEntry = null;
    if (runtimeId is not null)
    {
        RegistryLoadResult registry = await new ManagedRuntimeRegistry(managedRoot!).LoadAsync(
            cancellationToken);
        if (registry.Errors.Count > 0)
        {
            foreach (string error in registry.Errors)
            {
                Console.Error.WriteLine(error);
            }

            return 2;
        }

        ManagedRuntimeEntry[] matches = registry.Entries.Where(entry =>
                entry.Id.Equals(runtimeId, StringComparison.OrdinalIgnoreCase)
                && entry.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            Console.Error.WriteLine(
                "The requested runtime ID and Provider do not identify exactly one registered installation; no global selection was changed.");
            return 2;
        }

        expectedEntry = matches[0];
        if (expectedEntry.Kind != kind)
        {
            Console.Error.WriteLine(
                $"The requested runtime ID and Provider belong to {expectedEntry.Kind}, not {kind}; no global selection was changed.");
            return 2;
        }
    }

    RuntimeArchitecture selectionArchitecture = CurrentRuntimeArchitecture();
    if (expectedEntry is not null
        && expectedEntry.Architecture != selectionArchitecture)
    {
        Console.Error.WriteLine(
            $"The requested runtime is {expectedEntry.Architecture}, but 'use' selects registrations for the current CLI process architecture ({selectionArchitecture}); no global selection was changed.");
        return 2;
    }

    ManagedGlobalRuntimeSelectionResult selection =
        await new ManagedGlobalRuntimeSelectionService(managedRoot!).SetAsync(
            kind,
            selector!,
            selectionArchitecture,
            expectedEntry,
            cancellationToken: cancellationToken);
    if (!selection.Success)
    {
        foreach (string error in selection.Errors)
        {
            Console.Error.WriteLine(error);
        }

        if (selection.Errors.Any(error => error.Contains(
                "Multiple Providers",
                StringComparison.Ordinal)))
        {
            Console.Error.WriteLine(
                "Choose an exact candidate with --runtime-id and --provider; no global selection was changed.");
        }

        Console.Error.WriteLine(
            $"Selection was limited to the current CLI process architecture ({selectionArchitecture}); no global selection was changed.");

        return 2;
    }

    ManagedRuntimeEntry entry = selection.Entry!;
    Console.WriteLine($"Global {kind} selection: {selector} -> {entry.Version}");
    Console.WriteLine($"Runtime ID: {entry.Id}");
    Console.WriteLine($"Provider: {entry.ProviderId}");
    Console.WriteLine($"Executable: {entry.ExecutablePath}");
    return 0;
}

static bool TryParseUseOptions(
    string[] args,
    out string? runtimeId,
    out string? providerId,
    out string? error)
{
    runtimeId = null;
    providerId = null;
    error = null;
    bool globalSeen = false;
    bool runtimeIdSeen = false;
    bool providerSeen = false;
    bool rootSeen = false;

    for (int index = 2; index < args.Length; index++)
    {
        string argument = args[index];
        if (argument.Equals("--global", StringComparison.OrdinalIgnoreCase))
        {
            if (globalSeen)
            {
                error = "--global can be specified only once; no global selection was changed.";
                return false;
            }

            globalSeen = true;
            continue;
        }

        bool isRuntimeId = argument.Equals("--runtime-id", StringComparison.OrdinalIgnoreCase);
        bool isProvider = argument.Equals("--provider", StringComparison.OrdinalIgnoreCase);
        bool isRoot = argument.Equals("--root", StringComparison.OrdinalIgnoreCase);
        if (!isRuntimeId && !isProvider && !isRoot)
        {
            error = argument.StartsWith("-", StringComparison.Ordinal)
                ? $"Unknown use option '{argument}'; no global selection was changed."
                : $"Unexpected use argument '{argument}'; no global selection was changed.";
            return false;
        }

        bool alreadySeen = isRuntimeId
            ? runtimeIdSeen
            : isProvider
                ? providerSeen
                : rootSeen;
        if (alreadySeen)
        {
            error = $"{argument} can be specified only once; no global selection was changed.";
            return false;
        }

        if (index + 1 >= args.Length
            || string.IsNullOrWhiteSpace(args[index + 1])
            || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            error = $"{argument} requires a value; no global selection was changed.";
            return false;
        }

        string value = args[++index];
        if (isRuntimeId)
        {
            runtimeIdSeen = true;
            runtimeId = value;
        }
        else if (isProvider)
        {
            providerSeen = true;
            providerId = value;
        }
        else
        {
            rootSeen = true;
        }
    }

    if (!globalSeen)
    {
        error = "--global is required; no global selection was changed.";
        return false;
    }

    if (runtimeIdSeen != providerSeen)
    {
        error = "A global exact selection requires both --runtime-id and --provider; no global selection was changed.";
        return false;
    }

    if (runtimeId is not null
        && (!TryValidateUseIdentity("--runtime-id", runtimeId, out error)
            || !TryValidateUseIdentity("--provider", providerId!, out error)))
    {
        return false;
    }

    return true;
}

static bool TryParseUseRuntimeKind(string value, out RuntimeKind kind)
{
    if (value.Equals("node", StringComparison.OrdinalIgnoreCase))
    {
        kind = RuntimeKind.NodeJs;
        return true;
    }

    bool isNamedKind = Enum.GetNames<RuntimeKind>().Any(
        name => name.Equals(value, StringComparison.OrdinalIgnoreCase));
    if (!isNamedKind)
    {
        kind = default;
        return false;
    }

    return TryParseRuntimeKind(value, out kind);
}

static bool TryValidateUseIdentity(string option, string value, out string? error)
{
    if (value.Length > 256
        || !value.Equals(value.Trim(), StringComparison.Ordinal)
        || value.Any(char.IsControl))
    {
        error = $"{option} contains an invalid global selection identity; no global selection was changed.";
        return false;
    }

    error = null;
    return true;
}

static void PrintUseUsage(TextWriter output)
{
    output.WriteLine(
        "Usage: autoenvplus use <python|node|java|dotnet|msvc|llvm|mingw|cmake|ninja> <selector> --global [--runtime-id id --provider id] [--root directory]");
    output.WriteLine(
        "  Exact identity options must be supplied together; selection is limited to the current CLI process architecture.");
}

static async Task<int> RunWhichAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || !TryParseRuntimeKind(args[0], out RuntimeKind kind))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus which <runtime> [--runtime-id id] [--provider id] [--project directory] [--root directory]");
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
    if (!TryCreateSessionProfile(
            kind,
            out RuntimeProfile? session,
            out string? sessionRuntimeId,
            out string? sessionProviderId,
            out string? sessionError))
    {
        Console.Error.WriteLine(sessionError);
        return 2;
    }
    if (!TryApplySessionIdentityOptions(
            args,
            ref sessionRuntimeId,
            ref sessionProviderId,
            out sessionError))
    {
        Console.Error.WriteLine(sessionError);
        return 1;
    }

    ManagedRuntimeResolutionResult result = await new ManagedRuntimeResolutionService(managedRoot!).ResolveAsync(
        kind,
        projectPath,
        session,
        CurrentRuntimeArchitecture(),
        sessionRuntimeId,
        sessionProviderId,
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
    Console.WriteLine($"Runtime ID: {result.Entry.Id}");
    Console.WriteLine($"Provider:   {result.Entry.ProviderId}");
    Console.WriteLine($"Executable: {result.Entry.ExecutablePath}");
    return 0;
}

static async Task<int> RunExecAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || !TryParseRuntimeKind(args[0], out RuntimeKind kind))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus exec <runtime> [--runtime-id id] [--provider id] [--project directory] [--root directory] -- [arguments]");
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

    if (!TryCreateSessionProfile(
            kind,
            out RuntimeProfile? session,
            out string? sessionRuntimeId,
            out string? sessionProviderId,
            out string? sessionError))
    {
        Console.Error.WriteLine(sessionError);
        return 2;
    }
    if (!TryApplySessionIdentityOptions(
            options,
            ref sessionRuntimeId,
            ref sessionProviderId,
            out sessionError))
    {
        Console.Error.WriteLine(sessionError);
        return 1;
    }

    ManagedRuntimeResolutionResult resolved = await new ManagedRuntimeResolutionService(managedRoot!).ResolveAsync(
        kind,
        projectPath,
        session,
        CurrentRuntimeArchitecture(),
        sessionRuntimeId,
        sessionProviderId,
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
    else if (entry.Kind == RuntimeKind.DotNet)
    {
        startInfo.Environment["DOTNET_ROOT"] = entry.InstallRoot;
        startInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
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
            "Usage: autoenvplus tool <pip|pip3|npm|npx|javac|jar|clang++|g++> [--runtime-id id] [--provider id] [--project directory] [--root directory] -- [arguments]");
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

    NetworkSettings? networkSettings = await LoadProviderSourceNetworkSettingsAsync(
        managedRoot!,
        GetManagedToolNetworkId(args[0])!,
        cancellationToken);
    if (networkSettings is null
        || !TryResolveManagedToolNetworkSettings(
            networkSettings,
            args[0],
            out EffectiveNetworkSettings? effectiveNetworkSettings))
    {
        return 2;
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

    if (!TryCreateSessionProfile(
            runtimeKind,
            out RuntimeProfile? session,
            out string? sessionRuntimeId,
            out string? sessionProviderId,
            out string? sessionError))
    {
        Console.Error.WriteLine(sessionError);
        return 2;
    }
    if (!TryApplySessionIdentityOptions(
            options,
            ref sessionRuntimeId,
            ref sessionProviderId,
            out sessionError))
    {
        Console.Error.WriteLine(sessionError);
        return 1;
    }

    ManagedToolCommandResult resolved = await new ManagedToolCommandResolver(managedRoot!).ResolveAsync(
        args[0],
        projectPath,
        session,
        sessionRuntimeId,
        sessionProviderId,
        CurrentRuntimeArchitecture(),
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

    ToolNetworkEnvironment.Apply(
        startInfo.Environment,
        args[0],
        effectiveNetworkSettings!);

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

static async Task<int> RunProviderAsync(
    string[] args,
    bool pluginsOnly,
    CancellationToken cancellationToken)
{
    if (args.Length == 0)
    {
        ShowProviderUsage(pluginsOnly);
        return 1;
    }

    string command = args[0].ToLowerInvariant();
    if (command is not "list"
        and not "inspect"
        and not "import"
        and not "enable"
        and not "disable"
        and not "delete")
    {
        return UnknownProviderCommand(pluginsOnly);
    }

    if (!TryValidateProviderCommandArguments(args, command, out string? argumentError))
    {
        Console.Error.WriteLine(argumentError);
        return 1;
    }

    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    try
    {
        RuntimeProviderPluginStore store = new(managedRoot!);
        return command switch
        {
            "list" => await RunProviderListAsync(
                args,
                pluginsOnly,
                store,
                cancellationToken),
            "inspect" => await RunProviderInspectAsync(
                args,
                pluginsOnly,
                store,
                cancellationToken),
            "import" => await RunProviderImportAsync(args, store, cancellationToken),
            "enable" or "disable" or "delete" => await RunProviderMutationAsync(
                args,
                store,
                cancellationToken),
            _ => throw new InvalidOperationException("The provider command was not validated."),
        };
    }
    catch (RuntimeProviderPluginException exception)
    {
        string field = string.IsNullOrWhiteSpace(exception.Field)
            ? string.Empty
            : $" at {exception.Field}";
        Console.Error.WriteLine(
            $"Provider plugin error [{exception.Code}]{field}: {exception.Message}");
        return 2;
    }
}

static bool TryValidateProviderCommandArguments(
    string[] args,
    string command,
    out string? error)
{
    int positionalCount = command == "list" ? 1 : 2;
    if (args.Length < positionalCount)
    {
        error = "The provider command is missing a required argument.";
        return false;
    }

    HashSet<string> flags = new(StringComparer.OrdinalIgnoreCase) { "--json" };
    HashSet<string> valueOptions = new(StringComparer.OrdinalIgnoreCase) { "--root" };
    if (command == "list")
    {
        valueOptions.Add("--kind");
    }

    if (command is "import" or "enable" or "disable" or "delete")
    {
        flags.Add("--yes");
    }

    HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    for (int index = positionalCount; index < args.Length; index++)
    {
        string argument = args[index];
        if (flags.Contains(argument))
        {
            if (!seen.Add(argument))
            {
                error = $"{argument} can be specified only once.";
                return false;
            }

            continue;
        }

        if (valueOptions.Contains(argument))
        {
            if (!seen.Add(argument))
            {
                error = $"{argument} can be specified only once.";
                return false;
            }

            if (++index >= args.Length
                || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"{argument} requires a value.";
                return false;
            }

            continue;
        }

        error = "The provider command contains an unsupported option or extra argument.";
        return false;
    }

    error = null;
    return true;
}

static async Task<int> RunProviderListAsync(
    string[] args,
    bool pluginsOnly,
    RuntimeProviderPluginStore store,
    CancellationToken cancellationToken)
{
    RuntimeKind? kindFilter = null;
    if (TryFindOptionIndex(args, "--kind", out int kindIndex))
    {
        if (kindIndex + 1 >= args.Length
            || !TryParseRuntimeKind(args[kindIndex + 1], out RuntimeKind parsedKind))
        {
            Console.Error.WriteLine("--kind requires a supported runtime kind.");
            return 1;
        }

        kindFilter = parsedKind;
    }

    RuntimeProviderPluginListResult loaded = await store.ListAsync(cancellationToken);
    List<CliProviderView> providers = pluginsOnly
        ? []
        : GetBuiltInProviderViews().ToList();
    providers.AddRange(loaded.Plugins.Select(CreatePluginProviderView));
    if (kindFilter is RuntimeKind kind)
    {
        providers = providers.Where(provider => provider.RuntimeKind == kind).ToList();
    }

    providers = providers
        .OrderBy(provider => provider.RuntimeKind)
        .ThenBy(provider => provider.ProviderId, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new CliProviderListView(providers, loaded.Errors),
            CreateJsonOptions()));
    }
    else
    {
        Console.WriteLine(pluginsOnly
            ? "AutoEnvPlus runtime provider plugins"
            : "AutoEnvPlus runtime providers");
        foreach (CliProviderView provider in providers)
        {
            string state = provider.Source == "built-in"
                ? "built-in"
                : provider.IsEnabled ? "enabled" : "disabled";
            Console.WriteLine(
                $"  {provider.ProviderId,-34} {provider.RuntimeKind,-8} [{state}]");
            Console.WriteLine(
                $"    {provider.DisplayName} by {provider.Vendor}; {DescribeProviderCatalog(provider)}");
            if (provider.Source == "plugin")
            {
                Console.WriteLine($"    Trust: {provider.AuthenticityNotice}");
            }
        }

        if (providers.Count == 0)
        {
            Console.WriteLine(kindFilter is null
                ? "  No runtime provider plugins are installed."
                : "  No runtime providers match the selected kind.");
        }

        PrintPluginListErrors(loaded.Errors);
    }

    return loaded.Success ? 0 : 2;
}

static async Task<int> RunProviderInspectAsync(
    string[] args,
    bool pluginsOnly,
    RuntimeProviderPluginStore store,
    CancellationToken cancellationToken)
{
    if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            $"Usage: autoenvplus {(pluginsOnly ? "plugin" : "provider")} inspect <provider-id> [--root directory] [--json]");
        return 1;
    }

    if (!pluginsOnly)
    {
        CliProviderView? builtIn = GetBuiltInProviderViews().FirstOrDefault(provider =>
            provider.ProviderId.Equals(args[1], StringComparison.OrdinalIgnoreCase));
        if (builtIn is not null)
        {
            if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(JsonSerializer.Serialize(builtIn, CreateJsonOptions()));
            }
            else
            {
                PrintProviderDetails(builtIn);
            }

            return 0;
        }
    }

    if (!RuntimeProviderPluginIds.TryGetPluginId(args[1], out string pluginId))
    {
        Console.Error.WriteLine("The provider ID is invalid.");
        return 1;
    }

    RuntimeProviderPluginListResult loaded = await store.ListAsync(cancellationToken);
    RuntimeProviderPluginDescriptor? descriptor = loaded.Plugins.FirstOrDefault(plugin =>
        plugin.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
    if (descriptor is null)
    {
        Console.Error.WriteLine("The requested provider plugin is not installed.");
        PrintPluginListErrors(loaded.Errors);
        return 2;
    }

    if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                provider = CreatePluginProviderView(descriptor),
                manifest = descriptor.Manifest,
                errors = loaded.Errors,
            },
            CreateJsonOptions()));
        return loaded.Success ? 0 : 2;
    }

    PrintProviderDetails(CreatePluginProviderView(descriptor));
    Console.WriteLine($"  Manifest:    {descriptor.ManifestPath}");
    Console.WriteLine(
        $"  Download hosts: {string.Join(", ", descriptor.Manifest.Releases.SelectMany(release => release.Assets).Select(asset => asset.DownloadUri.IdnHost).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))}");
    PrintPluginListErrors(loaded.Errors);
    return loaded.Success ? 0 : 2;
}

static async Task<int> RunProviderImportAsync(
    string[] args,
    RuntimeProviderPluginStore store,
    CancellationToken cancellationToken)
{
    if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus plugin import <manifest.json> [--root directory] [--yes] [--json]");
        return 1;
    }

    if (!TryResolvePluginManifestPath(args[1], out string? manifestPath, out string? pathError))
    {
        Console.Error.WriteLine(pathError);
        return 1;
    }

    RuntimeProviderPluginImportPreview preview = await store.PreviewImportAsync(
        manifestPath!,
        cancellationToken);
    bool apply = args.Contains("--yes", StringComparer.OrdinalIgnoreCase);
    RuntimeProviderPluginDescriptor? imported = apply
        ? await store.ImportAsync(preview, cancellationToken)
        : null;

    if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                action = "import",
                applied = apply,
                defaultState = "disabled",
                sourceFile = Path.GetFileName(preview.SourcePath),
                provider = imported is null
                    ? CreatePluginManifestProviderView(preview.Manifest, isEnabled: false)
                    : CreatePluginProviderView(imported),
                preview.ReleaseCount,
                preview.AssetCount,
                preview.DownloadHosts,
                preview.HashAlgorithms,
            },
            CreateJsonOptions()));
        return 0;
    }

    Console.WriteLine("Runtime provider plugin import plan");
    Console.WriteLine($"  Source:      {preview.SourcePath}");
    Console.WriteLine($"  Provider:    {preview.Manifest.ProviderId}");
    Console.WriteLine($"  Name:        {preview.Manifest.DisplayName}");
    Console.WriteLine($"  Tool:        {preview.Manifest.LanguageToolId}");
    Console.WriteLine($"  Adapter:     {preview.Manifest.Kind}");
    Console.WriteLine(
        $"  Contents:    {preview.ReleaseCount} release(s), {preview.AssetCount} asset(s)");
    Console.WriteLine($"  Hosts:       {string.Join(", ", preview.DownloadHosts)}");
    Console.WriteLine("  Initial state: disabled");
    Console.WriteLine($"  Trust:       {DeclarativeRuntimeCatalogProvider.AuthenticityNotice}");
    if (!apply)
    {
        Console.WriteLine("Preview only: no files were changed. Add --yes to import this exact manifest.");
        return 0;
    }

    Console.WriteLine(
        $"Imported {imported!.ProviderId} in the disabled state. Inspect it, then enable it explicitly.");
    return 0;
}

static async Task<int> RunProviderMutationAsync(
    string[] args,
    RuntimeProviderPluginStore store,
    CancellationToken cancellationToken)
{
    if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus plugin <enable|disable|delete> <plugin:id> [--root directory] [--yes] [--json]");
        return 1;
    }

    if (!RuntimeProviderPluginIds.TryGetPluginId(args[1], out string pluginId))
    {
        Console.Error.WriteLine("The provider plugin ID is invalid.");
        return 1;
    }

    RuntimeProviderPluginListResult loaded = await store.ListAsync(cancellationToken);
    RuntimeProviderPluginDescriptor? current = loaded.Plugins.FirstOrDefault(plugin =>
        plugin.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
    if (current is null)
    {
        Console.Error.WriteLine("The requested provider plugin is not installed.");
        PrintPluginListErrors(loaded.Errors);
        return 2;
    }

    string action = args[0].ToLowerInvariant();
    bool apply = args.Contains("--yes", StringComparer.OrdinalIgnoreCase);
    object? result = null;
    if (apply)
    {
        result = action switch
        {
            "enable" => await store.EnableAsync(pluginId, cancellationToken),
            "disable" => await store.DisableAsync(pluginId, cancellationToken),
            "delete" => await store.DeleteAsync(pluginId, cancellationToken),
            _ => null,
        };
    }

    if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                action,
                applied = apply,
                provider = CreatePluginProviderView(current),
                result,
                errors = loaded.Errors,
            },
            CreateJsonOptions()));
        return loaded.Success ? 0 : 2;
    }

    Console.WriteLine("Runtime provider plugin change plan");
    Console.WriteLine($"  Action:      {action}");
    Console.WriteLine($"  Provider:    {current.ProviderId} ({current.Manifest.DisplayName})");
    Console.WriteLine($"  Current:     {(current.IsEnabled ? "enabled" : "disabled")}");
    Console.WriteLine($"  Result:      {DescribeProviderMutationResult(action)}");
    if (!apply)
    {
        Console.WriteLine("Preview only: no files were changed. Add --yes to apply this exact plan.");
        return loaded.Success ? 0 : 2;
    }

    if (result is RuntimeProviderPluginDeleteResult deleted && deleted.CleanupPending)
    {
        Console.WriteLine(
            "The plugin was removed from active storage; a quarantined cleanup copy remains under the managed root.");
    }
    else
    {
        Console.WriteLine($"Provider plugin {action} completed.");
    }

    return loaded.Success ? 0 : 2;
}

static string DescribeProviderMutationResult(string action) => action switch
{
    "enable" => "enabled (third-party provider becomes selectable)",
    "disable" => "disabled (installed manifest is retained)",
    "delete" => "deleted (managed plugin manifest and state entry are removed)",
    _ => "unchanged",
};

static bool TryResolvePluginManifestPath(
    string value,
    out string? manifestPath,
    out string? error)
{
    manifestPath = null;
    error = null;
    if (string.IsNullOrWhiteSpace(value)
        || value.IndexOfAny(['\r', '\n', '\0']) >= 0
        || (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && !uri.IsFile))
    {
        error = "The plugin manifest must be an existing local JSON file.";
        return false;
    }

    try
    {
        string fullPath = Path.GetFullPath(value);
        if (!File.Exists(fullPath)
            || !Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            error = "The plugin manifest must be an existing local JSON file.";
            return false;
        }

        manifestPath = fullPath;
        return true;
    }
    catch (Exception exception) when (exception is ArgumentException
        or IOException
        or NotSupportedException
        or UnauthorizedAccessException
        or System.Security.SecurityException)
    {
        error = "The plugin manifest path is invalid or inaccessible.";
        return false;
    }
}

static void ShowProviderUsage(bool pluginsOnly)
{
    string command = pluginsOnly ? "plugin" : "provider";
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine(
        $"  autoenvplus {command} list [--kind runtime] [--root directory] [--json]");
    Console.Error.WriteLine(
        $"  autoenvplus {command} inspect <provider-id> [--root directory] [--json]");
    Console.Error.WriteLine(
        $"  autoenvplus {command} import <manifest.json> [--root directory] [--yes] [--json]");
    Console.Error.WriteLine(
        $"  autoenvplus {command} <enable|disable|delete> <plugin:id> [--root directory] [--yes] [--json]");
}

static int UnknownProviderCommand(bool pluginsOnly)
{
    ShowProviderUsage(pluginsOnly);
    return 1;
}

static void PrintPluginListErrors(IEnumerable<RuntimeProviderPluginError> errors)
{
    foreach (RuntimeProviderPluginError error in errors)
    {
        Console.Error.WriteLine($"Provider plugin warning [{error.Code}]: {error.Message}");
    }
}

static void PrintProviderDetails(CliProviderView provider)
{
    Console.WriteLine("Runtime provider");
    Console.WriteLine($"  ID:          {provider.ProviderId}");
    Console.WriteLine($"  Name:        {provider.DisplayName}");
    Console.WriteLine($"  Vendor:      {provider.Vendor}");
    Console.WriteLine($"  Runtime:     {provider.RuntimeKind}");
    Console.WriteLine($"  Source:      {provider.Source}");
    Console.WriteLine($"  State:       {(provider.IsEnabled ? "enabled" : "disabled")}");
    Console.WriteLine($"  Homepage:    {provider.Homepage}");
    Console.WriteLine($"  License:     {provider.License}");
    Console.WriteLine($"  Catalog:     {DescribeProviderCatalog(provider)}");
    Console.WriteLine($"  Trust:       {provider.AuthenticityNotice}");
}

static string DescribeProviderCatalog(CliProviderView provider) =>
    provider.ReleaseCount is int releases && provider.AssetCount is int assets
        ? $"{releases} release(s), {assets} asset(s)"
        : "loaded on demand";

static IReadOnlyList<CliProviderView> GetBuiltInProviderViews() =>
[
    new(
        PythonOrgCatalogProvider.ProviderName,
        "Python.org Windows embeddable distributions",
        "Python Software Foundation",
        RuntimeKind.Python,
        "built-in",
        true,
        "https://www.python.org/",
        "PSF-2.0",
        "AutoEnvPlus built-in provider policy",
        null,
        null),
    new(
        NodeJsCatalogProvider.ProviderName,
        "Node.js official Windows distributions",
        "OpenJS Foundation",
        RuntimeKind.NodeJs,
        "built-in",
        true,
        "https://nodejs.org/",
        "See vendor distribution terms",
        "AutoEnvPlus built-in provider policy",
        null,
        null),
    new(
        AdoptiumCatalogProvider.ProviderName,
        "Eclipse Temurin",
        "Eclipse Adoptium",
        RuntimeKind.Java,
        "built-in",
        true,
        "https://adoptium.net/",
        "See vendor distribution terms",
        "AutoEnvPlus built-in provider policy",
        null,
        null),
    new(
        DotNetSdkCatalogProvider.ProviderName,
        "Microsoft .NET SDK",
        "Microsoft",
        RuntimeKind.DotNet,
        "built-in",
        true,
        "https://dotnet.microsoft.com/",
        "See vendor distribution terms",
        "AutoEnvPlus built-in provider policy",
        null,
        null),
];

static CliProviderView CreatePluginProviderView(
    RuntimeProviderPluginDescriptor descriptor) =>
    CreatePluginManifestProviderView(descriptor.Manifest, descriptor.IsEnabled);

static CliProviderView CreatePluginManifestProviderView(
    RuntimeProviderPluginManifest manifest,
    bool isEnabled) => new(
        manifest.ProviderId,
        manifest.DisplayName,
        manifest.Vendor,
        manifest.Kind,
        "plugin",
        isEnabled,
        manifest.Homepage.AbsoluteUri,
        manifest.License,
        DeclarativeRuntimeCatalogProvider.AuthenticityNotice,
        manifest.Releases.Count,
        manifest.Releases.Sum(release => release.Assets.Count));

static async Task<int> RunNetworkAsync(
    string[] args,
    CancellationToken cancellationToken)
{
    if (args.Length == 0
        || !args[0].Equals("show", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus network show [global|tool-id] [--root directory] [--json]");
        return 1;
    }

    string target = "global";
    bool targetSpecified = false;
    for (int index = 1; index < args.Length; index++)
    {
        string argument = args[index];
        if (argument.Equals("--json", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (argument.Equals("--root", StringComparison.OrdinalIgnoreCase))
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                Console.Error.WriteLine("--root requires a value.");
                return 1;
            }

            continue;
        }

        if (argument.StartsWith("-", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Unsupported network option: {argument}");
            return 1;
        }

        if (targetSpecified)
        {
            Console.Error.WriteLine("network show accepts at most one scope.");
            return 1;
        }

        target = argument.Trim();
        targetSpecified = true;
    }

    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    NetworkSettingsStore store = new(managedRoot!);
    NetworkSettingsLoadResult loaded = await store.LoadAsync(cancellationToken);
    if (!loaded.Success || loaded.Settings is null)
    {
        PrintNetworkErrors(loaded.Errors);
        return 2;
    }

    bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
    NetworkSettings settings = loaded.Settings;
    if (target.Equals("global", StringComparison.OrdinalIgnoreCase))
    {
        GlobalNetworkSettings global = settings.Global!;
        object[] overrides = settings.Tools!
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (object)new
            {
                toolId = pair.Key,
                httpProxy = CreateNetworkOverrideView(pair.Value.HttpProxy),
                httpsProxy = CreateNetworkOverrideView(pair.Value.HttpsProxy),
                mirror = CreateNetworkOverrideView(pair.Value.Mirror),
            })
            .ToArray();
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    scope = "global",
                    settingsPath = store.SettingsPath,
                    httpProxy = RedactNetworkEndpoint(global.HttpProxy),
                    httpsProxy = RedactNetworkEndpoint(global.HttpsProxy),
                    noProxy = global.NoProxy,
                    mirror = RedactNetworkEndpoint(global.Mirror),
                    toolOverrides = overrides,
                },
                CreateJsonOptions()));
            return 0;
        }

        Console.WriteLine("Global network settings");
        Console.WriteLine($"  File:        {store.SettingsPath}");
        Console.WriteLine($"  HTTP proxy:  {DescribeNetworkEndpoint(global.HttpProxy)}");
        Console.WriteLine($"  HTTPS proxy: {DescribeNetworkEndpoint(global.HttpsProxy)}");
        Console.WriteLine($"  No proxy:    {DescribeNoProxy(global.NoProxy!)}");
        Console.WriteLine($"  Mirror:      {DescribeNetworkEndpoint(global.Mirror)}");
        Console.WriteLine($"  Overrides:   {overrides.Length}");
        foreach ((string toolId, ToolNetworkSettings tool) in settings.Tools!
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                $"    {toolId}: HTTP {DescribeNetworkOverride(tool.HttpProxy)}, "
                + $"HTTPS {DescribeNetworkOverride(tool.HttpsProxy)}, "
                + $"mirror {DescribeNetworkOverride(tool.Mirror)}");
        }

        return 0;
    }

    target = NormalizeNetworkScopeAlias(target);
    if (!NetworkToolIds.IsSupported(target))
    {
        Console.Error.WriteLine($"Unsupported network tool scope: {target}");
        Console.Error.WriteLine(
            "Supported scopes: "
            + string.Join(", ", NetworkToolIds.All.Order(StringComparer.OrdinalIgnoreCase)));
        return 1;
    }

    if (!TryResolveNetworkSettings(settings, target, out EffectiveNetworkSettings? effective))
    {
        return 2;
    }

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                scope = "tool",
                toolId = effective!.ToolId,
                httpProxy = RedactNetworkEndpointUri(effective.HttpProxy),
                httpsProxy = RedactNetworkEndpointUri(effective.HttpsProxy),
                noProxy = effective.NoProxy,
                mirror = RedactNetworkEndpointUri(effective.Mirror),
            },
            CreateJsonOptions()));
        return 0;
    }

    Console.WriteLine($"Effective network settings for {effective!.ToolId}");
    Console.WriteLine($"  HTTP proxy:  {DescribeNetworkEndpointUri(effective.HttpProxy)}");
    Console.WriteLine($"  HTTPS proxy: {DescribeNetworkEndpointUri(effective.HttpsProxy)}");
    Console.WriteLine($"  No proxy:    {DescribeNoProxy(effective.NoProxy)}");
    Console.WriteLine($"  Mirror:      {DescribeNetworkEndpointUri(effective.Mirror)}");
    return 0;
}

static async Task<int> RunDownloadAsync(
    string[] args,
    CancellationToken cancellationToken)
{
    if (args.Length == 0)
    {
        ShowDownloadUsage();
        return 1;
    }

    if (args[0].Equals("url", StringComparison.OrdinalIgnoreCase))
    {
        return await RunUrlDownloadAsync(args, cancellationToken);
    }

    if (args[0].Equals("import", StringComparison.OrdinalIgnoreCase))
    {
        return await RunDownloadImportAsync(args, cancellationToken);
    }

    if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
    {
        return await RunDownloadListAsync(args, cancellationToken);
    }

    ShowDownloadUsage();
    return 1;
}

static async Task<int> RunUrlDownloadAsync(
    string[] args,
    CancellationToken cancellationToken)
{
    if (args.Length < 2
        || !Uri.TryCreate(args[1], UriKind.Absolute, out Uri? sourceUri)
        || !sourceUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(sourceUri.Host)
        || !string.IsNullOrEmpty(sourceUri.UserInfo))
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus download url <https-url> [--file name] [--connections 1|2|4|8|16] [--sha256 hash|--sha512 hash] [--max-bytes count] [--overwrite] [--root directory] [--yes]");
        Console.Error.WriteLine("The source must be an absolute HTTPS URL without embedded credentials.");
        return 1;
    }

    if (!TryParseTransferOptions(
            args,
            2,
            allowConnections: true,
            out CliTransferOptions? options,
            out string? optionError))
    {
        Console.Error.WriteLine(optionError);
        return 1;
    }

    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    string inferredName;
    try
    {
        inferredName = Path.GetFileName(Uri.UnescapeDataString(sourceUri.AbsolutePath));
    }
    catch (UriFormatException)
    {
        Console.Error.WriteLine("The URL path contains an invalid escaped file name.");
        return 1;
    }

    string fileName = options!.FileName ?? inferredName;
    if (!TryValidateManagedFileName(fileName, out string? fileNameError))
    {
        Console.Error.WriteLine(fileNameError);
        return 1;
    }

    string libraryRoot = GetManagedDownloadLibraryRoot(managedRoot!);
    string targetPath = Path.Combine(libraryRoot, fileName);
    if (File.Exists(targetPath) && !options.Overwrite)
    {
        Console.Error.WriteLine(
            $"The managed library already contains '{fileName}'. Review and add --overwrite to replace it.");
        return 2;
    }

    NetworkSettings? settings = await LoadNetworkSettingsAsync(
        managedRoot!,
        cancellationToken);
    if (settings is null
        || !TryResolveNetworkSettings(
            settings,
            NetworkToolIds.Downloads,
            out EffectiveNetworkSettings? effectiveNetworkSettings))
    {
        return 2;
    }

    Console.WriteLine("Managed download plan");
    Console.WriteLine($"  Source:       {RedactNetworkEndpointUri(sourceUri)}");
    Console.WriteLine($"  Destination:  {targetPath}");
    Console.WriteLine($"  Connections:  {options.ConnectionCount}");
    Console.WriteLine($"  Maximum size: {FormatByteCount(options.MaximumBytes)}");
    Console.WriteLine($"  Integrity:    {DescribeIntegrity(options.Integrity)}");
    Console.WriteLine($"  Overwrite:    {options.Overwrite}");
    Console.WriteLine("  Execution:    downloaded content is stored but never executed automatically");
    if (!options.Yes)
    {
        Console.WriteLine("Preview only: no files were changed. Add --yes to execute this exact plan.");
        return 0;
    }

    using HttpClient client = NetworkHttpClientFactory.Create(
        effectiveNetworkSettings!,
        Timeout.InfiniteTimeSpan);
    ManagedSegmentedDownloader downloader = new(client, libraryRoot);
    SegmentedDownloadResult result;
    try
    {
        result = await downloader.DownloadAsync(
            new SegmentedDownloadRequest(
                sourceUri,
                fileName,
                options.ConnectionCount,
                options.MaximumBytes,
                options.Integrity,
                options.Overwrite),
            new ConsoleManagedTransferProgress(),
            cancellationToken);
    }
    catch (HttpRequestException exception)
    {
        string status = exception.StatusCode is null
            ? "transport failure"
            : $"HTTP {(int)exception.StatusCode.Value}";
        Console.Error.WriteLine(
            $"Managed download failed ({status}). The source query and credentials were not logged.");
        return 2;
    }

    Console.WriteLine("Managed download completed.");
    Console.WriteLine($"  File:         {result.FilePath}");
    Console.WriteLine($"  Size:         {FormatByteCount(result.TotalBytes)}");
    Console.WriteLine($"  Content SHA-256: {result.ContentSha256}");
    Console.WriteLine($"  Mode:         {result.TransferMode} ({result.SegmentCount} segment(s))");
    if (result.FallbackReason is DownloadFallbackReason fallbackReason)
    {
        Console.WriteLine($"  Range fallback: {fallbackReason}");
    }

    if (result.HasVerifiedExpectedHash)
    {
        Console.WriteLine(
            $"  Expected {result.ExpectedHashAlgorithm!.Value.DisplayName()}: verified {result.VerifiedHash}");
    }

    return 0;
}

static async Task<int> RunDownloadImportAsync(
    string[] args,
    CancellationToken cancellationToken)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine(
            "Usage: autoenvplus download import <local-file> [--file name] [--sha256 hash|--sha512 hash] [--max-bytes count] [--overwrite] [--root directory] [--yes]");
        return 1;
    }

    if (!TryParseTransferOptions(
            args,
            2,
            allowConnections: false,
            out CliTransferOptions? options,
            out string? optionError))
    {
        Console.Error.WriteLine(optionError);
        return 1;
    }

    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    string sourcePath;
    FileInfo source;
    try
    {
        sourcePath = Path.GetFullPath(args[1]);
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine("The local package to import was not found.");
            return 1;
        }

        FileAttributes attributes = File.GetAttributes(sourcePath);
        if ((attributes & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0)
        {
            Console.Error.WriteLine(
                "The local import source must be a regular file and cannot be a reparse point.");
            return 1;
        }

        source = new FileInfo(sourcePath);
    }
    catch (Exception exception) when (exception is ArgumentException
        or NotSupportedException
        or PathTooLongException)
    {
        Console.Error.WriteLine($"The local import path is invalid: {exception.Message}");
        return 1;
    }

    if (source.Length > options!.MaximumBytes)
    {
        Console.Error.WriteLine(
            $"The local file is {source.Length} bytes, exceeding the {options.MaximumBytes}-byte limit.");
        return 1;
    }

    string fileName = options.FileName ?? source.Name;
    if (!TryValidateManagedFileName(fileName, out string? fileNameError))
    {
        Console.Error.WriteLine(fileNameError);
        return 1;
    }

    string libraryRoot = GetManagedDownloadLibraryRoot(managedRoot!);
    string targetPath = Path.Combine(libraryRoot, fileName);
    if (sourcePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("The selected file is already inside the managed library.");
        return 1;
    }

    if (File.Exists(targetPath) && !options.Overwrite)
    {
        Console.Error.WriteLine(
            $"The managed library already contains '{fileName}'. Review and add --overwrite to replace it.");
        return 2;
    }

    Console.WriteLine("Managed local import plan");
    Console.WriteLine($"  Source:       {sourcePath}");
    Console.WriteLine($"  Destination:  {targetPath}");
    Console.WriteLine($"  Size:         {FormatByteCount(source.Length)}");
    Console.WriteLine($"  Integrity:    {DescribeIntegrity(options.Integrity)}");
    Console.WriteLine($"  Overwrite:    {options.Overwrite}");
    Console.WriteLine("  Execution:    imported content is stored but never executed automatically");
    if (!options.Yes)
    {
        Console.WriteLine("Preview only: no files were changed. Add --yes to execute this exact plan.");
        return 0;
    }

    LocalPackageImportResult result = await new LocalPackageImportService(libraryRoot).ImportAsync(
        new LocalPackageImportRequest(
            sourcePath,
            fileName,
            options.MaximumBytes,
            options.Integrity,
            options.Overwrite),
        new ConsoleManagedTransferProgress(),
        cancellationToken);
    Console.WriteLine("Managed local import completed.");
    Console.WriteLine($"  File:     {result.FilePath}");
    Console.WriteLine($"  Size:     {FormatByteCount(result.TotalBytes)}");
    Console.WriteLine($"  Content SHA-256: {result.ContentSha256}");
    if (result.HasVerifiedExpectedHash)
    {
        Console.WriteLine(
            $"  Expected {result.ExpectedHashAlgorithm!.Value.DisplayName()}: verified {result.VerifiedHash}");
    }

    return 0;
}

static async Task<int> RunDownloadListAsync(
    string[] args,
    CancellationToken cancellationToken)
{
    for (int index = 1; index < args.Length; index++)
    {
        if (args[index].Equals("--json", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (args[index].Equals("--root", StringComparison.OrdinalIgnoreCase))
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                Console.Error.WriteLine("--root requires a value.");
                return 1;
            }

            continue;
        }

        Console.Error.WriteLine($"Unsupported download list option: {args[index]}");
        return 1;
    }

    if (!TryGetManagedRoot(args, out string? managedRoot, out string? rootError))
    {
        Console.Error.WriteLine(rootError);
        return 1;
    }

    string libraryRoot = GetManagedDownloadLibraryRoot(managedRoot!);
    ManagedDownloadLibrary library = new(libraryRoot);
    IReadOnlyList<ManagedDownloadLibraryItem> items = await library.ListFilesAsync(
        cancellationToken);
    if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(JsonSerializer.Serialize(items, CreateJsonOptions()));
        return 0;
    }

    Console.WriteLine("Managed download library");
    Console.WriteLine($"  Root: {libraryRoot}");
    foreach (ManagedDownloadLibraryItem item in items)
    {
        Console.WriteLine(
            $"  {item.FileName}  {FormatByteCount(item.SizeBytes)}  "
            + $"{item.Origin?.ToString() ?? "unknown"}");
        Console.WriteLine($"    {item.FilePath}");
        if (!string.IsNullOrWhiteSpace(item.ContentSha256))
        {
            Console.WriteLine($"    Content SHA-256 recorded at commit: {item.ContentSha256}");
        }

        if (item.HasVerifiedExpectedHash)
        {
            Console.WriteLine(
                $"    Expected {item.ExpectedHashAlgorithm!.Value.DisplayName()}: revalidated {item.VerifiedHash}");
        }
        else if (item.HasRecordedExpectedHashEvidence)
        {
            Console.WriteLine(item.ContentIdentityChanged
                ? "    Recorded expected-hash evidence is stale: current bytes changed."
                : "    Expected-hash evidence was recorded at commit but was not revalidated.");
        }
    }

    if (items.Count == 0)
    {
        Console.WriteLine("  No managed downloads or imports were found.");
    }

    return 0;
}

static void ShowDownloadUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine(
        "  autoenvplus download url <https-url> [--file name] [--connections 1|2|4|8|16] [--sha256 hash|--sha512 hash] [--max-bytes count] [--overwrite] [--root directory] [--yes]");
    Console.Error.WriteLine(
        "  autoenvplus download import <local-file> [--file name] [--sha256 hash|--sha512 hash] [--max-bytes count] [--overwrite] [--root directory] [--yes]");
    Console.Error.WriteLine("  autoenvplus download list [--root directory] [--json]");
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
    Console.WriteLine("  Commands:  python, python3, pip, pip3, node, npm, npx, java, javac, jar, dotnet, cl, clang, clang++, gcc, g++, cmake, ninja");
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
    Console.WriteLine("Open a new terminal before using python/node/java/dotnet directly.");
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
    Console.WriteLine("  Variables:    AUTOENVPLUS_PYTHON_VERSION, AUTOENVPLUS_NODE_VERSION, AUTOENVPLUS_JAVA_VERSION, AUTOENVPLUS_DOTNET_VERSION");
    Console.WriteLine("  Exact pins:   AUTOENVPLUS_<KIND>_RUNTIME_ID and AUTOENVPLUS_<KIND>_RUNTIME_PROVIDER_ID (session only)");
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

        if (plan.NetworkSummary.Applied)
        {
            ProjectTerminalNetworkSummary network = plan.NetworkSummary;
            string pipMirror = !network.PipEnvironmentApplied
                ? "not applied"
                : network.PipMirrorConfigured ? "configured" : "official";
            string npmMirror = !network.NpmEnvironmentApplied
                ? "not applied"
                : network.NpmMirrorConfigured ? "configured" : "official";
            Console.WriteLine(
                $"  Network: proxy={network.ProxySource}; HTTP={(network.HttpProxyConfigured ? "configured" : "direct")}; "
                + $"HTTPS={(network.HttpsProxyConfigured ? "configured" : "direct")}; no-proxy={network.NoProxyEntryCount}");
            Console.WriteLine(
                $"    pip mirror={pipMirror}; npm registry={npmMirror}; "
                + $"removed inherited variables={plan.EnvironmentRemovals.Count}");
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

static async Task<NetworkSettings?> LoadNetworkSettingsAsync(
    string managedRoot,
    CancellationToken cancellationToken)
{
    NetworkSettingsLoadResult loaded = await new NetworkSettingsStore(managedRoot)
        .LoadAsync(cancellationToken);
    if (!loaded.Success || loaded.Settings is null)
    {
        PrintNetworkErrors(loaded.Errors);
        return null;
    }

    return loaded.Settings;
}

static async Task<NetworkSettings?> LoadProviderSourceNetworkSettingsAsync(
    string managedRoot,
    string networkToolId,
    CancellationToken cancellationToken)
{
    ProviderSourceNetworkSettingsLoadResult loaded =
        await new ProviderSourceNetworkSettingsLoader(managedRoot)
            .LoadForToolsAsync([networkToolId], cancellationToken);
    if (!loaded.Success || loaded.Settings is null)
    {
        foreach (string error in loaded.Errors)
        {
            Console.Error.WriteLine(error);
        }

        return null;
    }

    return loaded.Settings;
}

static bool TryResolveRuntimeNetworkSettings(
    NetworkSettings settings,
    string runtime,
    out EffectiveNetworkSettings? effective)
{
    if (!TryParseCatalogRuntimeKind(runtime, out RuntimeKind kind)
        || !NetworkToolIds.TryGetRuntimeScope(kind, out string toolId))
    {
        Console.Error.WriteLine(
            "The runtime provider must be python, node, java, dotnet, msvc, llvm, mingw, cmake, or ninja.");
        effective = null;
        return false;
    }

    return TryResolveNetworkSettings(settings, toolId, out effective);
}

static string NormalizeNetworkScopeAlias(string value) =>
    value.Trim().ToLowerInvariant() switch
    {
        "python" => NetworkToolIds.RuntimePython,
        "node" or "nodejs" => NetworkToolIds.RuntimeNode,
        "java" => NetworkToolIds.RuntimeJava,
        "dotnet" => NetworkToolIds.RuntimeDotNet,
        "cpp" or "c++" => NetworkToolIds.RuntimeCpp,
        "pip3" => NetworkToolIds.Pip,
        "npx" => NetworkToolIds.Npm,
        "download" => NetworkToolIds.Downloads,
        _ => value.Trim().ToLowerInvariant(),
    };

static bool TryResolveManagedToolNetworkSettings(
    NetworkSettings settings,
    string toolAlias,
    out EffectiveNetworkSettings? effective)
{
    string? toolId = GetManagedToolNetworkId(toolAlias);
    if (toolId is null)
    {
        Console.Error.WriteLine("No network scope is available for the selected managed tool.");
        effective = null;
        return false;
    }

    return TryResolveNetworkSettings(settings, toolId, out effective);
}

static string? GetManagedToolNetworkId(string toolAlias) =>
    toolAlias.Trim().ToLowerInvariant() switch
    {
        "pip" or "pip3" => NetworkToolIds.Pip,
        "npm" or "npx" => NetworkToolIds.Npm,
        "javac" or "jar" => NetworkToolIds.RuntimeJava,
        "clang++" or "g++" => NetworkToolIds.RuntimeCpp,
        _ => null,
    };

static bool TryResolveNetworkSettings(
    NetworkSettings settings,
    string toolId,
    out EffectiveNetworkSettings? effective)
{
    NetworkSettingsResolutionResult resolved = NetworkSettingsResolver.Resolve(settings, toolId);
    if (!resolved.Success || resolved.EffectiveSettings is null)
    {
        PrintNetworkErrors(resolved.Errors);
        effective = null;
        return false;
    }

    effective = resolved.EffectiveSettings;
    return true;
}

static void PrintNetworkErrors(IEnumerable<NetworkSettingsError> errors)
{
    foreach (NetworkSettingsError error in errors)
    {
        Console.Error.WriteLine(
            $"Network settings error [{error.Code}] at {error.Path}: {error.Message}");
    }
}

static string DescribeNetworkEndpoint(string? value) =>
    RedactNetworkEndpoint(value) ?? "disabled";

static string DescribeNetworkEndpointUri(Uri? value) =>
    RedactNetworkEndpointUri(value) ?? "disabled";

static string? RedactNetworkEndpoint(string? value)
{
    if (string.IsNullOrWhiteSpace(value)
        || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
    {
        return null;
    }

    return RedactNetworkEndpointUri(uri);
}

static string? RedactNetworkEndpointUri(Uri? value)
{
    if (value is null)
    {
        return null;
    }

    bool redactedQuery = !string.IsNullOrEmpty(value.Query);
    UriBuilder safe = new(value)
    {
        UserName = string.Empty,
        Password = string.Empty,
        Query = string.Empty,
        Fragment = string.Empty,
    };
    return safe.Uri.AbsoluteUri + (redactedQuery ? " [query redacted]" : string.Empty);
}

static string DescribeNetworkOverride(NetworkEndpointOverride? value)
{
    NetworkEndpointOverrideMode mode = value?.Mode ?? NetworkEndpointOverrideMode.Inherit;
    return mode == NetworkEndpointOverrideMode.Custom
        ? $"custom ({RedactNetworkEndpoint(value?.Value) ?? "invalid endpoint"})"
        : mode.ToString().ToLowerInvariant();
}

static CliNetworkOverrideView CreateNetworkOverrideView(NetworkEndpointOverride? value)
{
    NetworkEndpointOverrideMode mode = value?.Mode ?? NetworkEndpointOverrideMode.Inherit;
    return new CliNetworkOverrideView(
        mode.ToString().ToLowerInvariant(),
        mode == NetworkEndpointOverrideMode.Custom
            ? RedactNetworkEndpoint(value?.Value)
            : null);
}

static string DescribeNoProxy(IReadOnlyList<string> values) =>
    values.Count == 0 ? "none" : string.Join(", ", values);

static bool TryParseTransferOptions(
    string[] args,
    int startIndex,
    bool allowConnections,
    out CliTransferOptions? options,
    out string? error)
{
    const long defaultMaximumBytes = 100L * 1024 * 1024 * 1024;
    const long absoluteMaximumBytes = 1024L * 1024 * 1024 * 1024;
    string? fileName = null;
    int connections = 8;
    long maximumBytes = defaultMaximumBytes;
    PackageHashExpectation? integrity = null;
    bool overwrite = false;
    bool yes = false;
    HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

    for (int index = startIndex; index < args.Length; index++)
    {
        string argument = args[index];
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            options = null;
            error = $"Unexpected download argument: {argument}";
            return false;
        }

        if (!seen.Add(argument))
        {
            options = null;
            error = $"Download option {argument} was specified more than once.";
            return false;
        }

        if (argument.Equals("--yes", StringComparison.OrdinalIgnoreCase))
        {
            yes = true;
            continue;
        }

        if (argument.Equals("--overwrite", StringComparison.OrdinalIgnoreCase))
        {
            overwrite = true;
            continue;
        }

        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            options = null;
            error = $"{argument} requires a value.";
            return false;
        }

        string value = args[++index];
        if (argument.Equals("--root", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (argument.Equals("--file", StringComparison.OrdinalIgnoreCase))
        {
            fileName = value;
            continue;
        }

        if (argument.Equals("--connections", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowConnections)
            {
                options = null;
                error = "--connections is only valid for URL downloads.";
                return false;
            }

            if (!int.TryParse(value, out connections)
                || !ManagedSegmentedDownloader.SupportedConnectionCounts.Contains(connections))
            {
                options = null;
                error = "--connections must be one of 1, 2, 4, 8, or 16.";
                return false;
            }

            continue;
        }

        if (argument.Equals("--max-bytes", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(value, out maximumBytes)
                || maximumBytes < 1
                || maximumBytes > absoluteMaximumBytes)
            {
                options = null;
                error = $"--max-bytes must be an integer from 1 through {absoluteMaximumBytes}.";
                return false;
            }

            continue;
        }

        PackageHashAlgorithm? algorithm = argument.ToLowerInvariant() switch
        {
            "--sha256" => PackageHashAlgorithm.Sha256,
            "--sha512" => PackageHashAlgorithm.Sha512,
            _ => null,
        };
        if (algorithm is PackageHashAlgorithm selectedAlgorithm)
        {
            if (integrity is not null)
            {
                options = null;
                error = "Specify only one of --sha256 or --sha512.";
                return false;
            }

            if (!selectedAlgorithm.IsValidHash(value))
            {
                options = null;
                error = $"{argument} requires a valid {selectedAlgorithm.DisplayName()} hexadecimal hash.";
                return false;
            }

            integrity = new PackageHashExpectation(selectedAlgorithm, value.ToLowerInvariant());
            continue;
        }

        options = null;
        error = $"Unsupported download option: {argument}";
        return false;
    }

    options = new CliTransferOptions(
        fileName,
        connections,
        maximumBytes,
        integrity,
        overwrite,
        yes);
    error = null;
    return true;
}

static bool TryValidateManagedFileName(string? fileName, out string? error)
{
    if (string.IsNullOrWhiteSpace(fileName)
        || fileName.Length > 240
        || fileName is "." or ".."
        || fileName.IndexOfAny(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
        || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        || !fileName.Equals(Path.GetFileName(fileName), StringComparison.Ordinal)
        || fileName.EndsWith(" ", StringComparison.Ordinal)
        || fileName.EndsWith(".", StringComparison.Ordinal))
    {
        error = "The destination must be a safe file name without a path.";
        return false;
    }

    string baseName = fileName.Split('.')[0];
    bool reserved = baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
        || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
        || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
        || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
        || (baseName.Length == 4
            && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && baseName[3] is >= '1' and <= '9');
    if (reserved)
    {
        error = "The destination file name is reserved by Windows.";
        return false;
    }

    string extension = Path.GetExtension(fileName);
    if (!ManagedDownloadLibrary.AllowedExtensions.Contains(
            extension,
            StringComparer.OrdinalIgnoreCase))
    {
        error = "The destination extension is not approved. Supported extensions: "
            + string.Join(", ", ManagedDownloadLibrary.AllowedExtensions);
        return false;
    }

    error = null;
    return true;
}

static string GetManagedDownloadLibraryRoot(string managedRoot) =>
    Path.Combine(managedRoot, "downloads", "library");

static string DescribeIntegrity(PackageHashExpectation? integrity) =>
    integrity is null
        ? "content SHA-256 will be recorded; no expected hash was supplied"
        : $"verify {integrity.Algorithm.DisplayName()} {integrity.ExpectedHash}";

static string FormatByteCount(long bytes)
{
    string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
    double value = bytes;
    int unit = 0;
    while (value >= 1024 && unit < units.Length - 1)
    {
        value /= 1024;
        unit++;
    }

    return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
}

static IRuntimeCatalogProvider? CreateProvider(
    string runtime,
    HttpClient client,
    RuntimeArchitecture architecture,
    string[] args,
    RuntimeVersion? requestedVersion,
    Uri? mirror,
    out string? error)
{
    error = null;
    if (runtime.Equals("python", StringComparison.OrdinalIgnoreCase))
    {
        return new PythonOrgCatalogProvider(client, architecture, mirror);
    }

    if (runtime.Equals("node", StringComparison.OrdinalIgnoreCase)
        || runtime.Equals("nodejs", StringComparison.OrdinalIgnoreCase))
    {
        return new NodeJsCatalogProvider(client, mirror);
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

        return new AdoptiumCatalogProvider(client, feature, architecture, mirror);
    }

    if (runtime.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    {
        return new DotNetSdkCatalogProvider(client, architecture, mirror);
    }

    error = "The runtime provider must be python, node, java, or dotnet.";
    return null;
}

static async Task<CliProviderResolution> ResolveCatalogProviderAsync(
    string runtime,
    HttpClient client,
    RuntimeArchitecture architecture,
    string[] args,
    RuntimeVersion? requestedVersion,
    Uri? officialMirror,
    string managedRoot,
    CancellationToken cancellationToken)
{
    if (args.Count(argument => argument.Equals(
            "--provider",
            StringComparison.OrdinalIgnoreCase)) > 1)
    {
        return new(null, "--provider can be specified only once.");
    }

    if (!TryGetOption(args, "--provider", out string? requestedProvider, out string? optionError))
    {
        return new(null, optionError);
    }

    if (!TryParseRuntimeKind(runtime, out RuntimeKind expectedKind))
    {
        return new(
            null,
            "The runtime provider must be python, node, java, dotnet, msvc, llvm, mingw, cmake, or ninja.");
    }

    bool requiresExplicitPlugin = ToolchainRuntimeProviderPolicy.RequiresExplicitPlugin(
        expectedKind);
    if (requiresExplicitPlugin && requestedProvider is null)
    {
        return new(
            null,
            $"{expectedKind} has no built-in archive provider. Specify --provider plugin:<id>; "
            + "the separate 'toolchain install' command continues to use the built-in WinGet workflow.");
    }

    string? officialProviderId = requiresExplicitPlugin
        ? null
        : GetBuiltInProviderId(expectedKind);
    if (!requiresExplicitPlugin
        && (requestedProvider is null
            || requestedProvider.Equals(officialProviderId, StringComparison.OrdinalIgnoreCase)))
    {
        IRuntimeCatalogProvider? provider = CreateProvider(
            runtime,
            client,
            architecture,
            args,
            requestedVersion,
            officialMirror,
            out string? error);
        return provider is null
            ? new(null, error ?? "No built-in runtime provider is available.")
            : new(new CliProviderSelection(provider, IsThirdParty: false), null);
    }

    if (requestedProvider is null)
    {
        return new(null, "An explicit runtime provider is required.");
    }

    if (RuntimeProviderPluginIds.BuiltInProviderIds.Contains(requestedProvider))
    {
        return new(
            null,
            requiresExplicitPlugin
                ? "Toolchain archive catalogs require an explicit plugin:<id> provider; built-in archive providers are not available."
                : "The selected built-in provider does not support the requested runtime kind.");
    }

    if (!requestedProvider.StartsWith(
            RuntimeProviderPluginIds.Prefix,
            StringComparison.OrdinalIgnoreCase)
        || !RuntimeProviderPluginIds.TryGetPluginId(requestedProvider, out _))
    {
        return new(
            null,
            requiresExplicitPlugin
                ? "--provider must be a valid plugin:<id> value for this toolchain archive catalog."
                : $"--provider must be {officialProviderId} or a valid plugin:<id> value.");
    }

    try
    {
        RuntimeProviderPluginRegistry registry = new(
            new RuntimeProviderPluginStore(managedRoot));
        DeclarativeRuntimeCatalogProvider? provider = await registry.ResolveByIdAsync(
            requestedProvider,
            cancellationToken);
        if (provider is null)
        {
            return new(
                null,
                "The selected provider plugin is not installed and enabled.");
        }

        if (provider.Kind != expectedKind)
        {
            return new(
                null,
                "The selected provider plugin does not support the requested runtime kind.");
        }

        return new(new CliProviderSelection(provider, IsThirdParty: true), null);
    }
    catch (RuntimeProviderPluginException exception)
    {
        return new(
            null,
            $"Provider plugin error [{exception.Code}]: {exception.Message}");
    }
}

static string GetBuiltInProviderId(RuntimeKind kind) => kind switch
{
    RuntimeKind.Python => PythonOrgCatalogProvider.ProviderName,
    RuntimeKind.NodeJs => NodeJsCatalogProvider.ProviderName,
    RuntimeKind.Java => AdoptiumCatalogProvider.ProviderName,
    RuntimeKind.DotNet => DotNetSdkCatalogProvider.ProviderName,
    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
};

static string FormatProviderUri(Uri uri, bool isThirdParty) =>
    isThirdParty
        ? RedactNetworkEndpointUri(uri) ?? "HTTPS endpoint"
        : uri.AbsoluteUri;

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
    return Enum.TryParse(normalized, true, out kind) && Enum.IsDefined(kind);
}

static bool TryParseCatalogRuntimeKind(string value, out RuntimeKind kind) =>
    TryParseRuntimeKind(value, out kind)
    && (kind is RuntimeKind.Python
        or RuntimeKind.NodeJs
        or RuntimeKind.Java
        or RuntimeKind.DotNet
        || ToolchainRuntimeProviderPolicy.RequiresExplicitPlugin(kind));

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

    return ManagedRootResolver.TryResolve(configuredRoot, out managedRoot, out error);
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
    out string? runtimeId,
    out string? providerId,
    out string? error)
{
    string variableName = ManagedRuntimeSessionPin.GetVersionVariableName(kind);
    string? value = System.Environment.GetEnvironmentVariable(variableName);
    if (string.IsNullOrWhiteSpace(value))
    {
        profile = null;
    }
    else if (!VersionSelector.TryParse(value, out VersionSelector? selector))
    {
        profile = null;
        runtimeId = null;
        providerId = null;
        error = $"Environment variable {variableName} contains an invalid selector: {value}";
        return false;
    }
    else
    {
        profile = new RuntimeProfile(
            new Dictionary<RuntimeKind, VersionSelector> { [kind] = selector! });
    }

    string runtimeIdVariable = ManagedRuntimeSessionPin.GetRuntimeIdVariableName(kind);
    if (!TryReadSessionIdentity(runtimeIdVariable, out runtimeId, out error))
    {
        providerId = null;
        return false;
    }

    string providerIdVariable = ManagedRuntimeSessionPin.GetProviderIdVariableName(kind);
    if (!TryReadSessionIdentity(providerIdVariable, out providerId, out error))
    {
        return false;
    }

    error = null;
    return true;
}

static bool TryApplySessionIdentityOptions(
    string[] args,
    ref string? runtimeId,
    ref string? providerId,
    out string? error)
{
    int runtimeIdCount = args.Count(argument => argument.Equals(
        "--runtime-id",
        StringComparison.OrdinalIgnoreCase));
    int providerCount = args.Count(argument => argument.Equals(
        "--provider",
        StringComparison.OrdinalIgnoreCase));
    if (runtimeIdCount > 1 || providerCount > 1)
    {
        error = "--runtime-id and --provider can each be specified only once.";
        return false;
    }

    if (runtimeIdCount == 0 && providerCount == 0)
    {
        error = null;
        return true;
    }

    if (runtimeIdCount == 0)
    {
        error = "--provider requires --runtime-id for an exact session pin.";
        return false;
    }

    if (!TryGetOption(args, "--runtime-id", out string? requestedRuntimeId, out error)
        || !TryValidateSessionIdentity("--runtime-id", requestedRuntimeId, out error))
    {
        return false;
    }

    string? requestedProviderId = null;
    if (providerCount > 0
        && (!TryGetOption(args, "--provider", out requestedProviderId, out error)
            || !TryValidateSessionIdentity("--provider", requestedProviderId, out error)))
    {
        return false;
    }

    runtimeId = requestedRuntimeId;
    providerId = requestedProviderId;
    error = null;
    return true;
}

static bool TryReadSessionIdentity(
    string variableName,
    out string? value,
    out string? error)
{
    value = System.Environment.GetEnvironmentVariable(variableName);
    if (string.IsNullOrEmpty(value))
    {
        value = null;
        error = null;
        return true;
    }

    if (!TryValidateSessionIdentity(variableName, value, out error))
    {
        value = null;
        return false;
    }

    error = null;
    return true;
}

static bool TryValidateSessionIdentity(
    string source,
    string? value,
    out string? error)
{
    if (string.IsNullOrEmpty(value)
        || value.Length > 512
        || value.StartsWith("--", StringComparison.Ordinal)
        || !value.Equals(value.Trim(), StringComparison.Ordinal)
        || value.Any(char.IsControl))
    {
        error = $"{source} contains an invalid session identity.";
        return false;
    }

    error = null;
    return true;
}

static RuntimeArchitecture CurrentRuntimeArchitecture() =>
    RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => RuntimeArchitecture.X86,
        Architecture.Arm64 => RuntimeArchitecture.Arm64,
        _ => RuntimeArchitecture.X64,
    };

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
    Console.WriteLine("  autoenvplus catalog <python|node|java|dotnet|msvc|llvm|mingw|cmake|ninja> [--provider official-id|plugin:id] [catalog options]");
    Console.WriteLine("  autoenvplus install <python|node|java|dotnet|msvc|llvm|mingw|cmake|ninja> <exact-version> [--provider official-id|plugin:id] [--arch value] [--root directory] [--yes]");
    Console.WriteLine("    Toolchain archive kinds require --provider plugin:<id>; toolchain install remains the WinGet workflow.");
    Console.WriteLine("  autoenvplus uninstall <managed-runtime-id> [--root directory] [--force] [--yes]");
    Console.WriteLine("  autoenvplus use <python|node|java|dotnet|msvc|llvm|mingw|cmake|ninja> <selector> --global [--runtime-id id --provider id] [--root directory]");
    Console.WriteLine("    Exact identity options must be supplied together; selection uses the current CLI process architecture.");
    Console.WriteLine("  autoenvplus which <runtime> [--runtime-id id] [--provider id] [--project directory] [--root directory]");
    Console.WriteLine("  autoenvplus exec <runtime> [--runtime-id id] [--provider id] [--project directory] [--root directory] -- [arguments]");
    Console.WriteLine("  autoenvplus tool <pip|pip3|npm|npx|javac|jar|clang++|g++> [--runtime-id id] [--provider id] [options] -- [arguments]");
    Console.WriteLine("  autoenvplus network show [global|tool-id] [--root directory] [--json]");
    Console.WriteLine("  autoenvplus download url <https-url> [--file name] [--connections 1|2|4|8|16] [--sha256 hash|--sha512 hash] [--root directory] [--yes]");
    Console.WriteLine("  autoenvplus download import <local-file> [--file name] [--sha256 hash|--sha512 hash] [--root directory] [--yes]");
    Console.WriteLine("  autoenvplus download list [--root directory] [--json]");
    Console.WriteLine("  autoenvplus provider list|inspect [options]");
    Console.WriteLine("  autoenvplus plugin list|inspect|import|enable|disable|delete [options]");
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

internal sealed record CliTransferOptions(
    string? FileName,
    int ConnectionCount,
    long MaximumBytes,
    PackageHashExpectation? Integrity,
    bool Overwrite,
    bool Yes);

internal sealed record CliNetworkOverrideView(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("endpoint")] string? Endpoint);

internal sealed record CliProviderView(
    string ProviderId,
    string DisplayName,
    string Vendor,
    RuntimeKind RuntimeKind,
    string Source,
    bool IsEnabled,
    string Homepage,
    string License,
    string AuthenticityNotice,
    int? ReleaseCount,
    int? AssetCount);

internal sealed record CliProviderListView(
    IReadOnlyList<CliProviderView> Providers,
    IReadOnlyList<RuntimeProviderPluginError> Errors);

internal sealed record CliProviderSelection(
    IRuntimeCatalogProvider Provider,
    bool IsThirdParty);

internal sealed record CliProviderResolution(
    CliProviderSelection? Selection,
    string? Error);

internal sealed class ConsoleManagedTransferProgress : IProgress<ManagedTransferProgress>
{
    private readonly object _sync = new();
    private ManagedTransferPhase? _lastPhase;
    private int _lastPercent = -1;
    private DateTimeOffset _lastReportUtc = DateTimeOffset.MinValue;

    public void Report(ManagedTransferProgress value)
    {
        lock (_sync)
        {
            int percent = value.TotalBytes is > 0
                ? (int)Math.Clamp(value.CompletedBytes * 100 / value.TotalBytes.Value, 0, 100)
                : -1;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool phaseChanged = value.Phase != _lastPhase;
            bool shouldReport = phaseChanged
                || percent >= _lastPercent + 5
                || now - _lastReportUtc >= TimeSpan.FromSeconds(2)
                || (value.TotalBytes is long total && value.CompletedBytes >= total);
            if (!shouldReport)
            {
                return;
            }

            _lastPhase = value.Phase;
            _lastPercent = percent;
            _lastReportUtc = now;
            string bytes = value.TotalBytes is long totalBytes
                ? $"{FormatBytes(value.CompletedBytes)} / {FormatBytes(totalBytes)}"
                : FormatBytes(value.CompletedBytes);
            string percentage = percent >= 0 ? $" ({percent}%)" : string.Empty;
            string segments = value.TotalSegments > 1
                ? $"; {value.CompletedSegments}/{value.TotalSegments} segments"
                : string.Empty;
            Console.WriteLine($"  [{value.Phase}] {bytes}{percentage}{segments}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
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
