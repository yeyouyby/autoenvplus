namespace AutoEnvPlus.Core.Diagnostics;

[Flags]
public enum DiagnosticScanScope
{
    None = 0,
    PathAndCommands = 1 << 0,
    ManagedTools = 1 << 1,
    ProjectEnvironment = 1 << 2,
    ProviderConfiguration = 1 << 3,
    StoragePressure = 1 << 4,
    ProviderConnectivity = 1 << 5,
}

public sealed record EnvironmentDiagnosticOptions
{
    public const long DefaultLowDiskFreeBytes = 10L * 1024 * 1024 * 1024;
    public const long DefaultLargeCacheBytes = 10L * 1024 * 1024 * 1024;
    public const int DefaultMaximumConnectivityEndpoints = 32;
    public const int DefaultMaximumCacheEntries = 50_000;
    public const int DefaultMaximumCacheDepth = 32;

    public static DiagnosticScanScope DefaultScopes { get; } =
        DiagnosticScanScope.PathAndCommands
        | DiagnosticScanScope.ManagedTools
        | DiagnosticScanScope.ProviderConfiguration;

    public DiagnosticScanScope Scopes { get; init; } = DefaultScopes;

    public string? ProjectRoot { get; init; }

    public long LowDiskFreeBytes { get; init; } = DefaultLowDiskFreeBytes;

    public double LowDiskFreeRatio { get; init; } = 0.10;

    public long LargeCacheBytes { get; init; } = DefaultLargeCacheBytes;

    public int MaximumCacheEntries { get; init; } = DefaultMaximumCacheEntries;

    public int MaximumCacheDepth { get; init; } = DefaultMaximumCacheDepth;

    public TimeSpan ConnectivityTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int MaximumConnectivityEndpoints { get; init; } =
        DefaultMaximumConnectivityEndpoints;

    internal void Validate()
    {
        const DiagnosticScanScope all = DiagnosticScanScope.PathAndCommands
            | DiagnosticScanScope.ManagedTools
            | DiagnosticScanScope.ProjectEnvironment
            | DiagnosticScanScope.ProviderConfiguration
            | DiagnosticScanScope.StoragePressure
            | DiagnosticScanScope.ProviderConnectivity;
        if (Scopes == DiagnosticScanScope.None || (Scopes & ~all) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Scopes),
                "At least one supported diagnostic scan scope must be selected.");
        }

        if (Scopes.HasFlag(DiagnosticScanScope.ProjectEnvironment)
            && string.IsNullOrWhiteSpace(ProjectRoot))
        {
            throw new ArgumentException(
                "A project directory is required for project environment diagnostics.",
                nameof(ProjectRoot));
        }

        if (LowDiskFreeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LowDiskFreeBytes));
        }

        if (LowDiskFreeRatio is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(LowDiskFreeRatio));
        }

        if (LargeCacheBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LargeCacheBytes));
        }

        if (MaximumCacheEntries is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCacheEntries));
        }

        if (MaximumCacheDepth is < 0 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCacheDepth));
        }

        if (ConnectivityTimeout is { } timeout
            && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectivityTimeout));
        }

        if (MaximumConnectivityEndpoints is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConnectivityEndpoints));
        }
    }
}
