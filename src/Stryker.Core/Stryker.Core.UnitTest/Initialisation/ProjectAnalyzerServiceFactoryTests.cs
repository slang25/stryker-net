using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Shouldly;
using Stryker.Abstractions.Options;
using Stryker.Core.Initialisation;
using Stryker.Core.Initialisation.ProjectAnalysis;
using Stryker.Utilities.Buildalyzer;

namespace Stryker.Core.UnitTest.Initialisation;

[TestClass]
public class ProjectAnalyzerServiceFactoryTests : TestBase
{
    [TestMethod]
    public void ShouldReturnBuildalyzerServiceByDefault()
    {
        var factory = BuildFactory(out var buildalyzerService, out _);

        var result = factory.Create(BuildAnalyzer.Buildalyzer);

        result.ShouldBe(buildalyzerService);
    }

    [TestMethod]
    public void ShouldReturnMSBuildWorkspaceServiceWhenSelected()
    {
        var factory = BuildFactory(out _, out var msbuildWorkspaceService);

        var result = factory.Create(BuildAnalyzer.MSBuildWorkspace);

        result.ShouldBe(msbuildWorkspaceService);
    }

    private static ProjectAnalyzerServiceFactory BuildFactory(
        out BuildalyzerProjectAnalyzerService buildalyzerService,
        out MSBuildWorkspaceProjectAnalyzerService msbuildWorkspaceService)
    {
        var nuget = new Mock<INugetRestoreProcess>().Object;
        var provider = new Mock<IBuildalyzerProvider>().Object;
        buildalyzerService = new BuildalyzerProjectAnalyzerService(
            provider, nuget, TestLoggerFactory.CreateLogger<BuildalyzerProjectAnalyzerService>());
        var hostManager = new MSBuildHostProcessManager(TestLoggerFactory.CreateLogger<MSBuildHostProcessManager>());
        msbuildWorkspaceService = new MSBuildWorkspaceProjectAnalyzerService(
            hostManager, nuget, TestLoggerFactory.CreateLogger<MSBuildWorkspaceProjectAnalyzerService>());
        return new ProjectAnalyzerServiceFactory(buildalyzerService, msbuildWorkspaceService);
    }
}
