using System;
using System.Linq;
using System.Threading;
using Buildalyzer;
using Microsoft.Extensions.Logging;
using Stryker.MSBuildHost.Contracts;
using Stryker.Utilities.Buildalyzer;
using Stryker.Utilities.ProjectAnalysis;
using Stryker.Utilities.ProjectAnalysis.MSBuildWorkspace;

namespace Stryker.Core.Initialisation.ProjectAnalysis;

/// <summary>
/// Delegates project analysis to the out-of-process Stryker.MSBuildHost via RPC and
/// adapts the returned DTO to <see cref="IAnalyzerResult"/> so downstream consumers
/// (extensions, SourceProjectInfo, CsharpCompilingProcess) work unchanged.
/// </summary>
public class MSBuildWorkspaceProjectAnalyzerService : IProjectAnalyzerService
{
    private readonly MSBuildHostProcessManager _hostManager;
    private readonly INugetRestoreProcess _nugetRestoreProcess;
    private readonly ILogger<MSBuildWorkspaceProjectAnalyzerService> _logger;

    public MSBuildWorkspaceProjectAnalyzerService(
        MSBuildHostProcessManager hostManager,
        INugetRestoreProcess nugetRestoreProcess,
        ILogger<MSBuildWorkspaceProjectAnalyzerService> logger)
    {
        _hostManager = hostManager ?? throw new ArgumentNullException(nameof(hostManager));
        _nugetRestoreProcess = nugetRestoreProcess ?? throw new ArgumentNullException(nameof(nugetRestoreProcess));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ProjectAnalysisOutcome AnalyzeProject(ProjectAnalysisRequest request)
    {
        // Mirror Buildalyzer: the initial pass analyzes every TFM in the project. The
        // request's TargetFramework is only used to narrow the final retry below.
        var input = new ProjectAnalysisInputDto
        {
            ProjectFilePath = request.ProjectFilePath,
            Configuration = request.Configuration,
            Platform = request.Platform,
        };

        var dto = AnalyzeAsync(input, CancellationToken.None).GetAwaiter().GetResult();

        if (!dto.OverallSuccess && ShouldRetryWithRestore(request, dto))
        {
            _logger.LogWarning("Project {ProjectFilePath} analysis failed. Stryker will retry after a nuget restore.", request.ProjectFilePath);
            if (request.DiagMode)
            {
                _logger.LogWarning("The MsBuild log is below.");
                _logger.LogInformation(dto.Log);
            }

            _nugetRestoreProcess.RestorePackages(request.SolutionPath, request.MsBuildPath);
            dto = AnalyzeAsync(input, CancellationToken.None).GetAwaiter().GetResult();

            if (!dto.OverallSuccess && !string.IsNullOrEmpty(request.TargetFramework))
            {
                var single = input with { TargetFramework = request.TargetFramework };
                dto = AnalyzeAsync(single, CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        var results = dto.FrameworkResults
            .Select(fr => (IAnalyzerResult)new MSBuildWorkspaceAnalyzerResult(fr))
            .ToArray();

        return new ProjectAnalysisOutcome(results, dto.Log, dto.OverallSuccess);
    }

    private async System.Threading.Tasks.Task<ProjectAnalysisResultDto> AnalyzeAsync(
        ProjectAnalysisInputDto input,
        CancellationToken cancellationToken)
    {
        var host = await _hostManager.GetHostAsync(input.ProjectFilePath, cancellationToken).ConfigureAwait(false);
        return await host.AnalyzeProjectAsync(input, cancellationToken).ConfigureAwait(false);
    }

    private static bool ShouldRetryWithRestore(ProjectAnalysisRequest request, ProjectAnalysisResultDto dto)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return false;
        }
        // Mirror Buildalyzer behaviour: only retry when at least one failing TFM is full framework.
        foreach (var fr in dto.FrameworkResults)
        {
            if (fr.Succeeded)
            {
                continue;
            }
            var dto2 = new MSBuildWorkspaceAnalyzerResult(fr);
            if (dto2.TargetsDesktop())
            {
                return true;
            }
        }
        return false;
    }
}
