using Stryker.Abstractions.Options;

namespace Stryker.Utilities.ProjectAnalysis;

public interface IProjectAnalyzerServiceFactory
{
    IProjectAnalyzerService Create(BuildAnalyzer backend);
}
