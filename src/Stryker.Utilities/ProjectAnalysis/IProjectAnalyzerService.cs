using System.Collections.Generic;
using Buildalyzer;

namespace Stryker.Utilities.ProjectAnalysis;

public interface IProjectAnalyzerService
{
    ProjectAnalysisOutcome AnalyzeProject(ProjectAnalysisRequest request);
}

public sealed record ProjectAnalysisRequest(
    string ProjectFilePath,
    string TargetFramework,
    string Configuration,
    string Platform,
    string MsBuildPath,
    string SolutionPath,
    bool DiagMode);

public sealed record ProjectAnalysisOutcome(
    IReadOnlyList<IAnalyzerResult> Results,
    string BuildLog,
    bool OverallSuccess);
