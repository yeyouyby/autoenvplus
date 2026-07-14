using System.Text.Json;

namespace AutoEnvPlus.Core.Projects;

public sealed record KnownProject(
    string ProjectRoot,
    DateTimeOffset LastSeenUtc);

public sealed class KnownProjectStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _managedRoot;
    private readonly string _storePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KnownProjectStore(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _storePath = Path.Combine(_managedRoot, "state", "projects.json");
    }

    public async Task<IReadOnlyList<KnownProject>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<KnownProject>> AddAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string root = Path.GetFullPath(projectRoot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<KnownProject> existing = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            List<KnownProject> projects = existing
                .Where(project => !project.ProjectRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
                .Append(new KnownProject(root, DateTimeOffset.UtcNow))
                .OrderBy(project => project.ProjectRoot, StringComparer.OrdinalIgnoreCase)
                .ToList();
            await SaveCoreAsync(projects, cancellationToken).ConfigureAwait(false);
            return projects;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<KnownProject>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storePath))
        {
            return [];
        }

        await using FileStream stream = new(
            _storePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            8_192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ProjectStoreDocument? document = await JsonSerializer.DeserializeAsync<ProjectStoreDocument>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (document is null || document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("The known-project store has an unsupported schema.");
        }

        return (document.Projects ?? [])
            .Where(project => !string.IsNullOrWhiteSpace(project.ProjectRoot))
            .Select(project => project with { ProjectRoot = Path.GetFullPath(project.ProjectRoot) })
            .ToArray();
    }

    private async Task SaveCoreAsync(
        IReadOnlyList<KnownProject> projects,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(directory);
        string temporary = _storePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                8_192,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new ProjectStoreDocument(CurrentSchemaVersion, projects.ToList()),
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _storePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record ProjectStoreDocument(
        int SchemaVersion,
        List<KnownProject>? Projects);
}
