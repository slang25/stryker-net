using System.Collections.Generic;

namespace Stryker.MSBuildHost.Contracts;

public sealed record ProjectAnalysisInputDto
{
    public string ProjectFilePath { get; init; } = string.Empty;
    public string? TargetFramework { get; init; }
    public string? Configuration { get; init; }
    public string? Platform { get; init; }
}

public sealed record ProjectAnalysisResultDto
{
    public IReadOnlyList<ProjectFrameworkAnalysisDto> FrameworkResults { get; init; } = [];
    public string Log { get; init; } = string.Empty;
    public bool OverallSuccess { get; init; }
}

public sealed record ProjectFrameworkAnalysisDto
{
    public string ProjectFilePath { get; init; } = string.Empty;
    public string TargetFramework { get; init; } = string.Empty;
    public bool Succeeded { get; init; }

    public Dictionary<string, string> Properties { get; init; } = new();
    public Dictionary<string, ProjectItemDto[]> Items { get; init; } = new();

    public string[] SourceFiles { get; init; } = [];
    public string[] References { get; init; } = [];
    public Dictionary<string, string[]> ReferenceAliases { get; init; } = new();
    public string[] ProjectReferences { get; init; } = [];
    public Dictionary<string, Dictionary<string, string>> PackageReferences { get; init; } = new();
    public string[] AnalyzerReferences { get; init; } = [];
    public string[] PreprocessorSymbols { get; init; } = [];
    public string[] AdditionalFiles { get; init; } = [];
}

public sealed record ProjectItemDto
{
    public string ItemSpec { get; init; } = string.Empty;
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Reports the SDK / dotnet runtime the host's MSBuildLocator picked for a given project,
/// plus the host's own <c>Environment.ProcessPath</c>. The client compares these to decide
/// whether to dispose and relaunch the host under the matching dotnet (two-phase startup).
/// </summary>
public sealed record MSBuildLocationDto
{
    /// <summary>SDK install directory (e.g. /usr/local/share/dotnet/sdk/10.0.203).</summary>
    public string MSBuildPath { get; init; } = string.Empty;
    /// <summary>The dotnet binary that hosts this SDK (e.g. /usr/local/share/dotnet/dotnet).</summary>
    public string DotnetPath { get; init; } = string.Empty;
    /// <summary>The host process's own ProcessPath (the dotnet that launched the host).</summary>
    public string HostProcessPath { get; init; } = string.Empty;
}
