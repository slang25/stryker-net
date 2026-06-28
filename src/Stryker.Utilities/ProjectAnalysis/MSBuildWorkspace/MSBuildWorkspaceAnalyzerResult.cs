using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Buildalyzer;
using Stryker.MSBuildHost.Contracts;

namespace Stryker.Utilities.ProjectAnalysis.MSBuildWorkspace;

/// <summary>
/// Adapter implementing Buildalyzer's <see cref="IAnalyzerResult"/> contract over a
/// <see cref="ProjectFrameworkAnalysisDto"/> produced by the out-of-process Stryker.MSBuildHost.
/// Lets Stryker's existing Buildalyzer-based consumers work unchanged when the user selects
/// the MSBuildWorkspace backend, without loading Microsoft.Build into the main process.
/// </summary>
public sealed class MSBuildWorkspaceAnalyzerResult : IAnalyzerResult
{
    public MSBuildWorkspaceAnalyzerResult(ProjectFrameworkAnalysisDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        ProjectFilePath = dto.ProjectFilePath;
        TargetFramework = dto.TargetFramework;
        Succeeded = dto.Succeeded;

        Properties = dto.Properties;
        Items = dto.Items.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Select(i => (IProjectItem)new HostProjectItem(i)).ToArray(),
            StringComparer.OrdinalIgnoreCase);

        SourceFiles = dto.SourceFiles;
        References = dto.References;
        ProjectReferences = dto.ProjectReferences;
        PackageReferences = dto.PackageReferences.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyDictionary<string, string>)kvp.Value,
            StringComparer.OrdinalIgnoreCase);
        AnalyzerReferences = dto.AnalyzerReferences;
        PreprocessorSymbols = dto.PreprocessorSymbols;
        AdditionalFiles = dto.AdditionalFiles;

        var aliasBuilder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>();
        foreach (var kvp in dto.ReferenceAliases)
        {
            aliasBuilder[kvp.Key] = kvp.Value.ToImmutableArray();
        }
        ReferenceAliases = aliasBuilder.ToImmutable();
    }

    public string[] AdditionalFiles { get; }
    public ProjectAnalyzer Analyzer => null;
    public string[] AnalyzerReferences { get; }
    public string Command => null;
    public string[] CompilerArguments => [];
    public string CompilerFilePath => null;
    public IReadOnlyDictionary<string, IProjectItem[]> Items { get; }
    public AnalyzerManager Manager => null;
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PackageReferences { get; }
    public string[] PreprocessorSymbols { get; }
    public string ProjectFilePath { get; }
    public Guid ProjectGuid => Guid.Empty;
    public IEnumerable<string> ProjectReferences { get; }
    public IReadOnlyDictionary<string, string> Properties { get; }
    public ImmutableDictionary<string, ImmutableArray<string>> ReferenceAliases { get; }
    public string[] References { get; }
    public string[] SourceFiles { get; }
    public bool Succeeded { get; }
    public string TargetFramework { get; }

    public string GetProperty(string name) =>
        Properties.TryGetValue(name, out var value) ? value : null;

    private sealed class HostProjectItem : IProjectItem
    {
        public HostProjectItem(ProjectItemDto dto)
        {
            ItemSpec = dto.ItemSpec;
            Metadata = dto.Metadata;
        }

        public string ItemSpec { get; }
        public IReadOnlyDictionary<string, string> Metadata { get; }
    }
}
