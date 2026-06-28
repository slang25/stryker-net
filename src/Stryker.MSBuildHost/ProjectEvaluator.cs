using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Stryker.MSBuildHost.Contracts;
using MSBuildProject = Microsoft.Build.Evaluation.Project;

namespace Stryker.MSBuildHost;

/// <summary>
/// Loads the project via Microsoft.Build, runs the design-time targets that populate
/// ReferencePath and AnalyzerReference items, and serializes results as a DTO.
/// Kept in its own class so the JIT only loads Microsoft.Build.* assemblies when one
/// of these methods is first invoked — after <see cref="StrykerMSBuildHost"/> has
/// registered MSBuildLocator.
/// </summary>
internal static class ProjectEvaluator
{
    // The standard MSBuild design-time targets that resolve references and analyzers
    // without compiling. Same set used by Roslyn's BuildHost.
    private static readonly string[] DesignTimeTargets =
    [
        "Compile",
    ];

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<ProjectAnalysisResultDto> AnalyzeAsync(
        ProjectAnalysisInputDto input,
        CancellationToken cancellationToken)
    {
        var log = new StringBuilder();
        var baseGlobalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DesignTimeBuild"] = "true",
            ["BuildingProject"] = "false",
            ["SkipCompilerExecution"] = "true",
            ["ProvideCommandLineArgs"] = "true",
            ["ContinueOnError"] = "ErrorAndContinue",
        };

        if (!string.IsNullOrEmpty(input.Configuration))
        {
            baseGlobalProperties["Configuration"] = input.Configuration!;
        }
        if (!string.IsNullOrEmpty(input.Platform))
        {
            baseGlobalProperties["Platform"] = input.Platform!;
        }

        // If the caller pinned a TFM (e.g. the retry-with-restore narrowing path), skip the
        // multi-TFM probe and analyze only that one. Otherwise discover all TFMs from the project.
        var tfms = !string.IsNullOrEmpty(input.TargetFramework)
            ? (IReadOnlyList<string>)[input.TargetFramework!]
            : ProbeTargetFrameworks(input.ProjectFilePath, baseGlobalProperties, log);
        var frameworkResults = new List<ProjectFrameworkAnalysisDto>(tfms.Count);
        var overallSuccess = true;

        foreach (var tfm in tfms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var globalProps = new Dictionary<string, string>(baseGlobalProperties, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(tfm))
            {
                globalProps["TargetFramework"] = tfm;
            }
            var (result, succeeded) = AnalyzeForFramework(input.ProjectFilePath, globalProps, log);
            frameworkResults.Add(result);
            if (!succeeded)
            {
                overallSuccess = false;
            }
        }

        return Task.FromResult(new ProjectAnalysisResultDto
        {
            FrameworkResults = frameworkResults,
            Log = log.ToString(),
            OverallSuccess = overallSuccess,
        });
    }

    private static IReadOnlyList<string> ProbeTargetFrameworks(
        string projectFilePath,
        Dictionary<string, string> baseGlobalProperties,
        StringBuilder log)
    {
        // Strip per-build properties for the neutral probe — we just want TargetFramework(s).
        var probeProps = new Dictionary<string, string>(baseGlobalProperties, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "TargetFramework", "DesignTimeBuild", "BuildingProject", "SkipCompilerExecution", "ProvideCommandLineArgs" })
        {
            probeProps.Remove(key);
        }

        var probeCollection = new ProjectCollection(probeProps);
        try
        {
            var project = probeCollection.LoadProject(projectFilePath);
            try
            {
                var multi = project.GetPropertyValue("TargetFrameworks");
                if (!string.IsNullOrWhiteSpace(multi))
                {
                    return multi.Split([';'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .ToArray();
                }
                var single = project.GetPropertyValue("TargetFramework");
                return string.IsNullOrWhiteSpace(single) ? [string.Empty] : [single];
            }
            finally
            {
                probeCollection.UnloadProject(project);
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"Failed to probe target frameworks for {projectFilePath}: {ex.Message}");
            return [string.Empty];
        }
    }

    private static (ProjectFrameworkAnalysisDto Dto, bool Succeeded) AnalyzeForFramework(
        string projectFilePath,
        Dictionary<string, string> globalProperties,
        StringBuilder log)
    {
        var collection = new ProjectCollection(globalProperties);
        try
        {
            var project = collection.LoadProject(projectFilePath);
            try
            {
                var projectInstance = project.CreateProjectInstance();
                var stringWriter = new StringWriter();
                var consoleLogger = new ConsoleLogger(LoggerVerbosity.Quiet, stringWriter.Write, _ => { }, () => { });

                var buildParams = new BuildParameters(collection)
                {
                    Loggers = [consoleLogger],
                    DetailedSummary = false,
                };
                var buildRequest = new BuildRequestData(projectInstance, DesignTimeTargets);

                BuildResult? result = null;
                try
                {
                    result = BuildManager.DefaultBuildManager.Build(buildParams, buildRequest);
                }
                catch (Exception ex)
                {
                    var tfmLabel = globalProperties.TryGetValue("TargetFramework", out var tfm) ? tfm : "<no TFM>";
                    log.AppendLine($"Build failed for {projectFilePath} ({tfmLabel}) : {ex.Message}");
                }

                log.Append(stringWriter.ToString());

                var succeeded = result is { OverallResult: BuildResultCode.Success };
                var dto = BuildDto(projectInstance, succeeded);
                return (dto, succeeded);
            }
            finally
            {
                collection.UnloadProject(project);
            }
        }
        finally
        {
            collection.Dispose();
        }
    }

    private static ProjectFrameworkAnalysisDto BuildDto(ProjectInstance projectInstance, bool succeeded)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in projectInstance.Properties)
        {
            properties[p.Name] = p.EvaluatedValue;
        }

        var items = projectInstance.Items
            .GroupBy(i => i.ItemType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(i => new ProjectItemDto
                {
                    ItemSpec = i.EvaluatedInclude,
                    Metadata = i.Metadata.ToDictionary(m => m.Name, m => m.EvaluatedValue, StringComparer.OrdinalIgnoreCase),
                }).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var sourceFiles = projectInstance.GetItems("Compile")
            .Select(i => i.GetMetadataValue("FullPath"))
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();

        // ReferencePath is populated by the ResolveAssemblyReferences target.
        var references = projectInstance.GetItems("ReferencePath")
            .Select(i => i.GetMetadataValue("FullPath"))
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();

        var projectReferences = projectInstance.GetItems("ProjectReference")
            .Select(i => i.GetMetadataValue("FullPath"))
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();

        var packageReferences = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in projectInstance.GetItems("PackageReference"))
        {
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in item.Metadata)
            {
                meta[m.Name] = m.EvaluatedValue;
            }
            packageReferences[item.EvaluatedInclude] = meta;
        }

        // Analyzers come from the Analyzer items (populated by ResolvePackageAssets / SDK targets).
        var analyzerReferences = projectInstance.GetItems("Analyzer")
            .Select(i => i.GetMetadataValue("FullPath"))
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();

        var preprocessorSymbols = ParsePreprocessorSymbols(projectInstance);

        var referenceAliases = ExtractReferenceAliases(projectInstance.GetItems("ReferencePath"));

        var additionalFiles = projectInstance.GetItems("AdditionalFiles")
            .Select(i => i.GetMetadataValue("FullPath"))
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();

        return new ProjectFrameworkAnalysisDto
        {
            ProjectFilePath = projectInstance.FullPath,
            TargetFramework = projectInstance.GetPropertyValue("TargetFramework"),
            Succeeded = succeeded,
            Properties = properties,
            Items = items,
            SourceFiles = sourceFiles,
            References = references,
            ReferenceAliases = referenceAliases,
            ProjectReferences = projectReferences,
            PackageReferences = packageReferences,
            AnalyzerReferences = analyzerReferences,
            PreprocessorSymbols = preprocessorSymbols,
            AdditionalFiles = additionalFiles,
        };
    }

    private static string[] ParsePreprocessorSymbols(ProjectInstance project)
    {
        var defines = project.GetPropertyValue("DefineConstants");
        if (string.IsNullOrWhiteSpace(defines))
        {
            return [];
        }
        return defines.Split([';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
    }

    private static Dictionary<string, string[]> ExtractReferenceAliases(ICollection<ProjectItemInstance> referencePathItems)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in referencePathItems)
        {
            var aliases = item.GetMetadataValue("Aliases");
            if (string.IsNullOrEmpty(aliases))
            {
                continue;
            }
            var path = item.GetMetadataValue("FullPath");
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }
            result[path] = aliases.Split([','], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
        }
        return result;
    }
}
