#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using StreamJsonRpc;
using Stryker.Abstractions.Exceptions;
using Stryker.MSBuildHost.Contracts;

namespace Stryker.Core.Initialisation.ProjectAnalysis;

internal enum BuildHostKind
{
    NetCore,
    NetFramework,
}

/// <summary>
/// Owns the lifetime of the Stryker.MSBuildHost child process(es). Spawns lazily on first
/// request, restarts on disconnect, and keeps at most one host per <see cref="BuildHostKind"/>
/// (NetCore for SDK-style projects, NetFramework for legacy on Windows).
/// </summary>
public sealed class MSBuildHostProcessManager : IAsyncDisposable
{
    private const string NetCoreHostFolder = "BuildHost-netcore";
    private const string Net472HostFolder = "BuildHost-net472";
    private const string NetCoreHostAssembly = "Stryker.MSBuildHost.dll";
    private const string Net472HostAssembly = "Stryker.MSBuildHost.exe";
    private const int ConnectTimeoutMs = 60_000;

    private readonly ILogger<MSBuildHostProcessManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<BuildHostKind, HostInstance> _instances = new();
    private bool _disposed;

    public MSBuildHostProcessManager(ILogger<MSBuildHostProcessManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Returns a live host proxy ready to analyze the given project. Detects the kind from the
    /// .csproj XML (SDK-style → netcore; otherwise → net472). NetCore additionally runs the
    /// two-phase startup dance: launch under default dotnet, ask for the project's matching SDK,
    /// dispose and relaunch under the matching dotnet if it differs.
    /// </summary>
    public async Task<IStrykerMSBuildHost> GetHostAsync(string projectFilePath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var kind = GetKindForProject(projectFilePath);
            if (!_instances.TryGetValue(kind, out var instance) || !instance.IsAlive)
            {
                instance?.Dispose();
                instance = await LaunchAsync(kind, dotnetPath: null, cancellationToken).ConfigureAwait(false);
                _instances[kind] = instance;
            }

            if (kind == BuildHostKind.NetCore)
            {
                instance = await EnsureMatchingDotnetAsync(instance, projectFilePath, cancellationToken).ConfigureAwait(false);
                _instances[kind] = instance;
            }

            return instance.Proxy;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Detects which build host kind to use for a project by inspecting the .csproj XML for
    /// SDK-style markers (Sdk attribute, &lt;Import Sdk=... /&gt;, &lt;Sdk /&gt;). Mirrors
    /// Roslyn's BuildHostProcessManager.GetKindForProject.
    /// </summary>
    private static BuildHostKind GetKindForProject(string projectFilePath)
    {
        try
        {
            var doc = XDocument.Load(projectFilePath);
            var root = doc.Root;
            if (root is null)
            {
                return BuildHostKind.NetFramework;
            }

            // SDK attribute on root: <Project Sdk="Microsoft.NET.Sdk">
            if (root.Attributes().Any(a => a.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase)))
            {
                return BuildHostKind.NetCore;
            }

            // <Import Sdk="..." />
            if (root.Elements().Any(e => e.Name.LocalName.Equals("Import", StringComparison.OrdinalIgnoreCase) &&
                                          e.Attributes().Any(a => a.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase))))
            {
                return BuildHostKind.NetCore;
            }

            // <Sdk Name="..." />
            if (root.Elements().Any(e => e.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase)))
            {
                return BuildHostKind.NetCore;
            }

            return BuildHostKind.NetFramework;
        }
        catch (Exception)
        {
            // If we can't read the csproj, default to netcore — the more common case and the
            // more cross-platform host (net472 only runs on Windows).
            return BuildHostKind.NetCore;
        }
    }

    private async Task<HostInstance> EnsureMatchingDotnetAsync(
        HostInstance instance,
        string projectFilePath,
        CancellationToken cancellationToken)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        if (instance.MatchedSdkForProjectDir is { } cachedDir && PathsEqual(cachedDir, projectDir))
        {
            return instance;
        }

        MSBuildLocationDto? location;
        try
        {
            location = await instance.Proxy.FindBestMSBuildAsync(projectFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Host couldn't report best MSBuild for {ProjectFilePath}; continuing without relaunch.", projectFilePath);
            return instance;
        }

        if (location is null || string.IsNullOrEmpty(location.DotnetPath) || !File.Exists(location.DotnetPath))
        {
            instance.MatchedSdkForProjectDir = projectDir;
            return instance;
        }

        if (PathsEqual(location.DotnetPath, location.HostProcessPath))
        {
            instance.MatchedSdkForProjectDir = projectDir;
            return instance;
        }

        _logger.LogInformation(
            "Relaunching Stryker.MSBuildHost under matching SDK dotnet '{TargetDotnet}' (was '{CurrentDotnet}').",
            location.DotnetPath, location.HostProcessPath);

        instance.Dispose();
        var relaunched = await LaunchAsync(BuildHostKind.NetCore, location.DotnetPath, cancellationToken).ConfigureAwait(false);
        relaunched.MatchedSdkForProjectDir = projectDir;
        return relaunched;
    }

    private static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparer);
    }

    private async Task<HostInstance> LaunchAsync(BuildHostKind kind, string? dotnetPath, CancellationToken cancellationToken)
    {
        if (kind == BuildHostKind.NetFramework && !OperatingSystem.IsWindows())
        {
            throw new InputException(
                "The .NET Framework MSBuild host is only supported on Windows. " +
                "Non-SDK-style projects cannot be analyzed via --build-analyzer msbuildworkspace on this platform; " +
                "use --build-analyzer buildalyzer or run Stryker on Windows.");
        }

        var (fileName, argList) = BuildSpawnArgs(kind, dotnetPath);
        var pipeName = Guid.NewGuid().ToString("N");

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in argList)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add(System.Globalization.CultureInfo.CurrentUICulture.Name);
        if (kind == BuildHostKind.NetCore)
        {
            psi.Environment["DOTNET_ROLL_FORWARD_TO_PRERELEASE"] = "1";
        }
        psi.Environment.Remove("MSBUILD_EXE_PATH");
        psi.Environment.Remove("MSBuildExtensionsPath");
        psi.Environment.Remove("MSBuildSDKsPath");

        _logger.LogDebug("Launching Stryker.MSBuildHost ({Kind}): {File} {Args} <pipeName>",
            kind, fileName, string.Join(' ', argList));

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to spawn host process.");
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogDebug("[MSBuildHost stderr] {Line}", e.Data);
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogDebug("[MSBuildHost stdout] {Line}", e.Data);
            }
        };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            if (!process.HasExited)
            {
                try { process.Kill(); } catch { /* best effort */ }
            }
            throw;
        }

        var formatter = new SystemTextJsonFormatter();
        var handler = new LengthHeaderMessageHandler(pipe.UsePipe(), formatter);
        var rpc = new JsonRpc(handler);
        var proxy = rpc.Attach<IStrykerMSBuildHost>();
        rpc.StartListening();

        return new HostInstance(process, pipe, rpc, proxy);
    }

    private static (string fileName, List<string> args) BuildSpawnArgs(BuildHostKind kind, string? dotnetPath)
    {
        switch (kind)
        {
            case BuildHostKind.NetCore:
            {
                var hostDll = ResolveHostBinaryPath(NetCoreHostFolder, NetCoreHostAssembly);
                return (dotnetPath ?? "dotnet", new List<string>
                {
                    "--roll-forward", "LatestMajor", hostDll,
                });
            }
            case BuildHostKind.NetFramework:
            {
                var hostExe = ResolveHostBinaryPath(Net472HostFolder, Net472HostAssembly);
                return (hostExe, []);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static string ResolveHostBinaryPath(string contentFolder, string fileName)
    {
        var assemblyDir = Path.GetDirectoryName(typeof(MSBuildHostProcessManager).Assembly.Location)
            ?? AppContext.BaseDirectory;

        // Primary: side-by-side via MSBuild deployment target.
        var sideBySide = Path.Combine(assemblyDir, contentFolder, fileName);
        if (File.Exists(sideBySide))
        {
            return sideBySide;
        }

        // Dev-time fallback: walk up to repo root then into Stryker.MSBuildHost/bin/<config>/<tfm>/.
        var devCandidate = TryFindDevTimeHost(assemblyDir, fileName);
        if (devCandidate is not null)
        {
            return devCandidate;
        }

        throw new FileNotFoundException(
            $"Could not locate Stryker.MSBuildHost. Looked next to '{assemblyDir}' for '{contentFolder}/{fileName}'.");
    }

    private static string? TryFindDevTimeHost(string startDir, string fileName)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Stryker.MSBuildHost", "bin");
            if (Directory.Exists(candidate))
            {
                foreach (var configDir in Directory.EnumerateDirectories(candidate))
                {
                    foreach (var tfmDir in Directory.EnumerateDirectories(configDir))
                    {
                        var path = Path.Combine(tfmDir, fileName);
                        if (File.Exists(path))
                        {
                            return path;
                        }
                    }
                }
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Test hook: returns the underlying <see cref="Process"/> of the cached host for the given
    /// kind, or null if none is cached. Used by integration tests to simulate host crashes.
    /// </summary>
    internal Process? GetHostProcessForTest(BuildHostKind kind) =>
        _instances.TryGetValue(kind, out var i) ? i.Process : null;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var (_, instance) in _instances)
            {
                try
                {
                    await instance.Proxy.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // host may already be dead — fall through to dispose
                }
                instance.Dispose();
            }
            _instances.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class HostInstance : IDisposable
    {
        public HostInstance(Process process, NamedPipeClientStream pipe, JsonRpc rpc, IStrykerMSBuildHost proxy)
        {
            Process = process;
            Pipe = pipe;
            Rpc = rpc;
            Proxy = proxy;
        }

        public Process Process { get; }
        public NamedPipeClientStream Pipe { get; }
        public JsonRpc Rpc { get; }
        public IStrykerMSBuildHost Proxy { get; }

        /// <summary>
        /// Last project directory whose required dotnet matched this host's runtime. Used to
        /// short-circuit the relaunch probe for subsequent projects under the same dir/SDK.
        /// </summary>
        public string? MatchedSdkForProjectDir { get; set; }

        public bool IsAlive => !Process.HasExited && Pipe.IsConnected && !Rpc.IsDisposed;

        public void Dispose()
        {
            try { Rpc.Dispose(); } catch { /* ignore */ }
            try { Pipe.Dispose(); } catch { /* ignore */ }
            if (!Process.HasExited)
            {
                try { Process.WaitForExit(2000); } catch { /* ignore */ }
                if (!Process.HasExited)
                {
                    try { Process.Kill(); } catch { /* ignore */ }
                }
            }
            try { Process.Dispose(); } catch { /* ignore */ }
        }
    }
}
