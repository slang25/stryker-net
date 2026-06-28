# Stryker.MSBuildHost

Out-of-process host that evaluates `.csproj` files via Microsoft.Build and returns the
results to Stryker over a named-pipe RPC. Used when the user runs Stryker with
`--build-analyzer msbuildworkspace`.

## Why a separate process?

`Microsoft.Build` requires `MSBuildLocator.RegisterMSBuildPath(...)` before any
`Microsoft.Build.*` types load. Calling that in the main `Stryker.CLI` process has two
problems:

1. **Env-var pollution.** `MSBuildLocator` sets `MSBUILD_EXE_PATH`,
   `MSBuildExtensionsPath`, and `MSBuildSDKsPath` on the process. Child `dotnet build`
   invocations spawned later by `InitialBuildProcess` inherit them and pick the wrong
   SDK (e.g. an SDK-8 MSBuild trying to build a `net10.0` project). Avoiding this in the
   parent meant we had to maintain a strip-list in `ProcessExecutor` — fragile and easy
   to forget on new spawn sites.

2. **Runtime / SDK coupling.** `MSBuildLocator` only considers SDKs compatible with the
   currently-running .NET runtime. A `Stryker.CLI` hosted on `net8.0` cannot find a
   `net10.0`-only SDK. The host being its own process lets us spawn it under whatever
   `dotnet` matches the *project's* required SDK (see [two-phase relaunch](#two-phase-relaunch)).

The pattern mirrors Roslyn's `MSBuildWorkspace` BuildHost — see
[`Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost`](https://github.com/dotnet/roslyn/tree/main/src/Workspaces/MSBuild/BuildHost)
for the reference design we copied.

## Process layout

```
Stryker.CLI (net8.0)
    │
    ├── MSBuildHostProcessManager   ── spawns ──▶ dotnet --roll-forward LatestMajor
    │     - one HostInstance per BuildHostKind                  Stryker.MSBuildHost.dll
    │     - lazy restart on disconnect                          (or directly  the .exe on net472)
    │     - StreamJsonRpc over NamedPipeClientStream
    │
    └── MSBuildWorkspaceProjectAnalyzerService
          - sends ProjectAnalysisInputDto → host
          - receives ProjectAnalysisResultDto
          - adapts DTO to IAnalyzerResult (MSBuildWorkspaceAnalyzerResult)
          - retries with INugetRestoreProcess for full-framework failures
```

The main process never references `Microsoft.Build.*` or
`Microsoft.CodeAnalysis.Workspaces.MSBuild`. Only `Stryker.MSBuildHost.Contracts` (DTOs +
the RPC interface) is shared.

## RPC protocol

- **Transport:** `NamedPipeServerStream` on the host, `NamedPipeClientStream` in the manager.
  Pipe name is a freshly-generated `Guid` passed as `argv[0]`.
- **Framing & serialization:** `LengthHeaderMessageHandler` + `SystemTextJsonFormatter`
  from `StreamJsonRpc` 2.24. (Roslyn rolls its own JSON framing to keep source-build
  compatibility — we don't need to.)
- **Contract:** `IStrykerMSBuildHost` in `Stryker.MSBuildHost.Contracts`. Three methods:
  `FindBestMSBuildAsync`, `AnalyzeProjectAsync`, `ShutdownAsync`.
- **Stdin** is closed immediately after spawn so MSBuild tasks that try to read from the
  console can't deadlock. Stdout/stderr are reserved for diagnostics; everything else
  flows over the pipe.

## Project-kind detection

`MSBuildHostProcessManager.GetKindForProject` reads the `.csproj` as XML and looks for
SDK-style markers in three places (mirroring Roslyn):

| Marker | Kind |
| --- | --- |
| `<Project Sdk="...">` root attribute | NetCore |
| `<Import Sdk="..." />` | NetCore |
| `<Sdk Name="..." />` | NetCore |
| (none of the above) | NetFramework |

The host is launched under one of two TFMs based on kind:

- **NetCore:** `Stryker.MSBuildHost.dll` (net8.0) launched via
  `dotnet --roll-forward LatestMajor`.
- **NetFramework:** `Stryker.MSBuildHost.exe` (net472 apphost) launched directly,
  Windows-only. On other platforms `LaunchAsync` throws `InputException` with a clear
  message — non-SDK projects can't be analyzed via this backend off Windows.

## Deferred MSBuildLocator

`MSBuildLocator.RegisterMSBuildPath` does **not** run at host startup. It's deferred to
the first `AnalyzeProjectAsync` call, when we know the project path and can pick the SDK
matching its directory (`global.json` honored via `WorkingDirectory`).

Critical detail: the methods that touch `Microsoft.Build.*` types live in
`ProjectEvaluator`, a separate class from `StrykerMSBuildHost`. This stops the JIT from
loading MSBuild types early — if MSBuild types load before locator runs, registration
silently no-ops.

## Two-phase relaunch

The first `GetHostAsync(projectPath)` call launches the host under the default `dotnet`.
The manager then calls `FindBestMSBuildAsync(projectPath)` on the running host, which
returns three paths:

- `MSBuildPath` — the SDK install dir the host's locator would pick.
- `DotnetPath` — the `dotnet` binary that hosts that SDK (computed as
  `<sdkRoot>/../../dotnet`).
- `HostProcessPath` — the host's own `Environment.ProcessPath`.

If `DotnetPath != HostProcessPath`, the manager disposes the host and relaunches with
`FileName = DotnetPath` explicitly. The relaunched host's locator now finds the
project's matching SDK and registration succeeds.

This covers the case where the user has SDK 10 installed in a private location but
`dotnet` on `PATH` resolves to SDK 8 — without the relaunch, the host would only find
SDK 8 and fail on `net10.0` projects.

## Deployment

`Stryker.CLI/Stryker.CLI.csproj` has a `DeployStrykerMSBuildHost` target that runs the
host's MSBuild twice (one TFM each) and emits `<Content>` items:

- `bin/Debug/<tfm>/BuildHost-netcore/` and `bin/Debug/<tfm>/BuildHost-net472/` for
  dev consumers.
- `tools/<tfm>/any/BuildHost-netcore/` and `tools/<tfm>/any/BuildHost-net472/` inside
  the `dotnet-stryker` nupkg.

`Microsoft.Build.*` runtime assemblies are filtered out (only `Microsoft.Build.Locator.dll`
ships). Locator resolves them from the SDK / VS install at runtime.

## Lifetime & crash recovery

- The manager keeps a `Dictionary<BuildHostKind, HostInstance>` — one persistent host per
  kind, reused across all `AnalyzeProjectAsync` calls in a single Stryker run.
- `HostInstance.IsAlive` checks `!Process.HasExited && Pipe.IsConnected && !Rpc.IsDisposed`.
  On the next `GetHostAsync`, a dead instance is disposed and a fresh process spawned.
- `MSBuildHostProcessManagerTests.RelaunchesAfterHostCrash` covers this via PID
  comparison after a deliberate `Process.Kill`.

## When **not** to use this backend

- **Mono/Linux for .NET Framework projects.** No Mono host variant (Roslyn ships one;
  we haven't). Use `--build-analyzer buildalyzer` or run on Windows.
- **The current default is still Buildalyzer.** This backend is selected by
  `--build-analyzer msbuildworkspace`. Keeping both lets users fall back if a
  project trips up one path.
