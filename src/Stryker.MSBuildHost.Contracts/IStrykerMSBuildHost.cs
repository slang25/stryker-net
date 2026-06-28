using System.Threading;
using System.Threading.Tasks;

namespace Stryker.MSBuildHost.Contracts;

/// <summary>
/// RPC contract implemented by the out-of-process Stryker.MSBuildHost.
/// Surfaces just enough project analysis surface for Stryker's IAnalyzerResult adapter.
/// </summary>
public interface IStrykerMSBuildHost
{
    /// <summary>
    /// Queries the best MSBuild instance for the given project (honoring global.json) without
    /// registering MSBuildLocator yet. Returns the matching SDK path, the dotnet executable
    /// hosting that SDK, and the host's own ProcessPath — the client uses these to decide
    /// whether to dispose and relaunch the host under the matching dotnet.
    /// </summary>
    Task<MSBuildLocationDto?> FindBestMSBuildAsync(string projectFilePath, CancellationToken cancellationToken);

    /// <summary>
    /// Evaluates the project (once per TFM if multi-targeted) and returns a DTO containing
    /// MSBuild properties, items, resolved references, analyzer paths, and preprocessor symbols.
    /// </summary>
    Task<ProjectAnalysisResultDto> AnalyzeProjectAsync(ProjectAnalysisInputDto input, CancellationToken cancellationToken);

    /// <summary>
    /// Graceful shutdown — server cancels its read loop and the process exits.
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken);
}
