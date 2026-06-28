using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using Stryker.Core.Initialisation.ProjectAnalysis;

namespace Stryker.Core.UnitTest.Initialisation;

/// <summary>
/// Integration tests for <see cref="MSBuildHostProcessManager"/>. These spawn real
/// Stryker.MSBuildHost child processes — they require the host binaries to be deployed
/// either side-by-side with Stryker.Core or under the src/Stryker.MSBuildHost/bin/
/// dev-time fallback path. CI runs them; they're slower than pure unit tests.
/// </summary>
[TestClass]
public class MSBuildHostProcessManagerTests : TestBase
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task RelaunchesAfterHostCrash()
    {
        var (projectPath, tempDir) = CreateMinimalSdkCsproj();
        try
        {
            await using var manager = new MSBuildHostProcessManager(
                TestLoggerFactory.CreateLogger<MSBuildHostProcessManager>());

            // First call: launches a host.
            var host1 = await manager.GetHostAsync(projectPath, CancellationToken.None);
            var location1 = await host1.FindBestMSBuildAsync(projectPath, CancellationToken.None);
            location1.ShouldNotBeNull("first launch should produce a working host");

            var process1 = manager.GetHostProcessForTest(BuildHostKind.NetCore);
            process1.ShouldNotBeNull();
            var pid1 = process1!.Id;

            // Simulate crash.
            process1.Kill();
            process1.WaitForExit(5_000).ShouldBeTrue("the killed host process should exit promptly");

            // Second call: manager should detect IsAlive=false and re-launch.
            var host2 = await manager.GetHostAsync(projectPath, CancellationToken.None);
            var location2 = await host2.FindBestMSBuildAsync(projectPath, CancellationToken.None);
            location2.ShouldNotBeNull("relaunched host should respond to RPC");

            var process2 = manager.GetHostProcessForTest(BuildHostKind.NetCore);
            process2.ShouldNotBeNull();
            process2!.Id.ShouldNotBe(pid1, "the relaunched host should be a different process");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static (string CsprojPath, string TempDir) CreateMinimalSdkCsproj()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stryker-msbuildhost-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var csprojPath = Path.Combine(dir, "Test.csproj");
        File.WriteAllText(csprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        return (csprojPath, dir);
    }
}
