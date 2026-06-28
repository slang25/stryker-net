using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Buildalyzer;
using Buildalyzer.Environment;
using Microsoft.Extensions.Logging;
using Stryker.Utilities.Buildalyzer;
using Stryker.Utilities.ProjectAnalysis;

namespace Stryker.Core.Initialisation.ProjectAnalysis;

public class BuildalyzerProjectAnalyzerService : IProjectAnalyzerService
{
    private readonly IBuildalyzerProvider _analyzerProvider;
    private readonly INugetRestoreProcess _nugetRestoreProcess;
    private readonly ILogger<BuildalyzerProjectAnalyzerService> _logger;

    public BuildalyzerProjectAnalyzerService(
        IBuildalyzerProvider analyzerProvider,
        INugetRestoreProcess nugetRestoreProcess,
        ILogger<BuildalyzerProjectAnalyzerService> logger)
    {
        _analyzerProvider = analyzerProvider ?? throw new ArgumentNullException(nameof(analyzerProvider));
        _nugetRestoreProcess = nugetRestoreProcess ?? throw new ArgumentNullException(nameof(nugetRestoreProcess));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ProjectAnalysisOutcome AnalyzeProject(ProjectAnalysisRequest request)
    {
        var logWriter = new StringWriter();
        var manager = _analyzerProvider.Provide(new AnalyzerManagerOptions { LogWriter = logWriter });

        if (!string.IsNullOrEmpty(request.Configuration))
        {
            manager.SetGlobalProperty("Configuration", request.Configuration);
        }

        if (!string.IsNullOrEmpty(request.Platform))
        {
            manager.SetGlobalProperty("Platform", request.Platform);
        }

        var project = manager.GetProject(request.ProjectFilePath);

        var env = new EnvironmentOptions();
        if (!string.IsNullOrEmpty(request.MsBuildPath))
        {
            env.EnvironmentVariables[EnvironmentVariables.MSBUILD_EXE_PATH] = request.MsBuildPath;
        }

        var buildResult = project.Build(env);

        var overallSuccess = ComputeOverallSuccess(project, buildResult);

        if (!overallSuccess)
        {
            if (request.DiagMode)
            {
                _logger.LogWarning("Project {ProjectFilePath} analysis failed. The MsBuild log is: {Log}",
                    request.ProjectFilePath, logWriter.ToString());
            }
            buildResult = RetryBuild(project, request, buildResult, logWriter, out overallSuccess);
        }

        return new ProjectAnalysisOutcome(
            buildResult.ToArray(),
            logWriter.ToString(),
            overallSuccess);
    }

    private IAnalyzerResults RetryBuild(IProjectAnalyzer project, ProjectAnalysisRequest request,
        IAnalyzerResults buildResult, StringWriter logWriter, out bool overallSuccess)
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT && buildResult.Any(r => !r.IsValid() && r.TargetsDesktop()))
        {
            _logger.LogWarning("Project {ProjectFilePath} analysis failed. Stryker will retry after a nuget restore.",
                request.ProjectFilePath);

            if (request.DiagMode)
            {
                _logger.LogWarning("The MsBuild log is below.");
                _logger.LogInformation(logWriter.ToString());
            }

            _nugetRestoreProcess.RestorePackages(
                request.SolutionPath,
                request.MsBuildPath ?? buildResult.First().MsBuildPath());
        }

        var buildOptions = new EnvironmentOptions { Restore = true };
        buildResult = project.Build(buildOptions);

        overallSuccess = project.ProjectFile.TargetFrameworks.Length > 0 &&
            Array.TrueForAll(project.ProjectFile.TargetFrameworks, tf => buildResult.Any(br => br.IsValidFor(tf)));

        if (!overallSuccess && !string.IsNullOrEmpty(request.TargetFramework))
        {
            buildResult = project.Build(request.TargetFramework);
            overallSuccess = buildResult.Any(br => br.IsValidFor(request.TargetFramework));
        }

        return buildResult;
    }

    private static bool ComputeOverallSuccess(IProjectAnalyzer project, IAnalyzerResults buildResult) =>
        buildResult.OverallSuccess ||
        Array.TrueForAll(project.ProjectFile.TargetFrameworks, tf => buildResult.Any(br => br.IsValidFor(tf)));
}
