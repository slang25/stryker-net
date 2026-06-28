using System;
using Stryker.Abstractions.Options;
using Stryker.Utilities.ProjectAnalysis;

namespace Stryker.Core.Initialisation.ProjectAnalysis;

public class ProjectAnalyzerServiceFactory : IProjectAnalyzerServiceFactory
{
    private readonly BuildalyzerProjectAnalyzerService _buildalyzerService;
    private readonly MSBuildWorkspaceProjectAnalyzerService _msbuildWorkspaceService;

    public ProjectAnalyzerServiceFactory(
        BuildalyzerProjectAnalyzerService buildalyzerService,
        MSBuildWorkspaceProjectAnalyzerService msbuildWorkspaceService)
    {
        _buildalyzerService = buildalyzerService ?? throw new ArgumentNullException(nameof(buildalyzerService));
        _msbuildWorkspaceService = msbuildWorkspaceService ?? throw new ArgumentNullException(nameof(msbuildWorkspaceService));
    }

    public IProjectAnalyzerService Create(BuildAnalyzer backend) => backend switch
    {
        BuildAnalyzer.MSBuildWorkspace => _msbuildWorkspaceService,
        _ => _buildalyzerService,
    };
}
