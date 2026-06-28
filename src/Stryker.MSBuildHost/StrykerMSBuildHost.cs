using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Stryker.MSBuildHost.Contracts;

namespace Stryker.MSBuildHost;

/// <summary>
/// Implements the RPC contract. MSBuildLocator is deferred to the first analysis call
/// so we can match the SDK to the project's directory (honoring global.json on netcore;
/// picking the highest installed VS MSBuild on netframework).
/// </summary>
public sealed class StrykerMSBuildHost : IStrykerMSBuildHost
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _msbuildRegistered;

    public Task<MSBuildLocationDto?> FindBestMSBuildAsync(string projectFilePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(projectFilePath))
        {
            throw new ArgumentException("projectFilePath must be supplied.", nameof(projectFilePath));
        }

        var instance = QueryBestSdkInstance(projectFilePath);
        if (instance is null)
        {
            return Task.FromResult<MSBuildLocationDto?>(null);
        }

#if NETFRAMEWORK
        // net472 host is launched as an apphost .exe, not via dotnet. The two-phase relaunch
        // dance only applies to netcore; return empty DotnetPath so the client skips it.
        var dotnetPath = string.Empty;
#else
        // netcore: SDK path is .../dotnet/sdk/<version>/. The hosting dotnet is two dirs up.
        var sdkPath = instance.MSBuildPath;
        var dotnetRoot = Path.GetDirectoryName(Path.GetDirectoryName(sdkPath));
        var dotnetBinary = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        var dotnetPath = dotnetRoot is null ? string.Empty : Path.Combine(dotnetRoot, dotnetBinary);
#endif

        return Task.FromResult<MSBuildLocationDto?>(new MSBuildLocationDto
        {
            MSBuildPath = instance.MSBuildPath,
            DotnetPath = dotnetPath,
            HostProcessPath = GetHostProcessPath(),
        });
    }

    public async Task<ProjectAnalysisResultDto> AnalyzeProjectAsync(ProjectAnalysisInputDto input, CancellationToken cancellationToken)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }
        if (string.IsNullOrEmpty(input.ProjectFilePath))
        {
            throw new ArgumentException("ProjectFilePath must be supplied.", nameof(input));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureMSBuildRegistered(input.ProjectFilePath);
            return await ProjectEvaluator.AnalyzeAsync(input, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Best-effort signal that the client is done. The actual shutdown happens when the
    // client disposes the named-pipe connection — that completes JsonRpc.Completion in
    // Program.cs, which lets the process exit. Implementing this as a no-op avoids a
    // RemoteMethodNotFoundException on the client side when it sends the call.
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void EnsureMSBuildRegistered(string projectFilePath)
    {
        if (_msbuildRegistered)
        {
            return;
        }

        var instance = QueryBestSdkInstance(projectFilePath)
            ?? throw new InvalidOperationException(
                $"No .NET SDK / MSBuild instance found for project '{projectFilePath}'.");

        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterMSBuildPath(instance.MSBuildPath);
        }
        _msbuildRegistered = true;
    }

    private static VisualStudioInstance? QueryBestSdkInstance(string projectFilePath)
    {
#if NETFRAMEWORK
        // net472: pick the highest installed Visual Studio MSBuild. VS-style installs only.
        var options = new VisualStudioInstanceQueryOptions
        {
            DiscoveryTypes = DiscoveryType.VisualStudioSetup,
        };
        return MSBuildLocator.QueryVisualStudioInstances(options)
            .OrderByDescending(i => i.Version)
            .FirstOrDefault();
#else
        // netcore: pick the SDK matching project dir (honors global.json).
        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        var options = new VisualStudioInstanceQueryOptions
        {
            DiscoveryTypes = DiscoveryType.DotNetSdk,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        return MSBuildLocator.QueryVisualStudioInstances(options).FirstOrDefault();
#endif
    }

    private static string GetHostProcessPath()
    {
#if NET6_0_OR_GREATER
        return Environment.ProcessPath ?? string.Empty;
#else
        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
#endif
    }
}
