using System.Net;
using System.Net.Sockets;
using CliWrap;
using Stryker.TestRunner.MicrosoftTestPlatform;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using StreamJsonRpc;
using Stryker.Abstractions;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.Coverage;
using Stryker.TestRunner.Results;
using Stryker.TestRunner.Tests;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

public sealed class MicrosoftTestPlatformRunner : ITestRunner
{
    private readonly string _id = Guid.NewGuid().ToString();
    private readonly TestSet _testSet = new();
    private readonly Dictionary<string, List<TestNode>> _testsByAssembly = new();
    private readonly Dictionary<string, MtpTestDescription> _testDescriptions = new();

    private string ControlVariableName => $"ACTIVE_MUTATION_{_id}";

    public bool DiscoverTests(string assembly)
    {
        if (string.IsNullOrEmpty(assembly))
        {
            return false;
        }

        if (!File.Exists(assembly))
        {
            return false;
        }

        var discoveryTask = DiscoverTestsInternalAsync(assembly);
        discoveryTask.Wait();
        var result = discoveryTask.Result;
        return result;
    }

    public ITestSet GetTests(IProjectAndTests project)
    {
        return _testSet;
    }

    public ITestRunResult InitialTest(IProjectAndTests project)
    {
        var assemblies = project.GetTestAssemblies();
        if (!assemblies.Any())
        {
            return new TestRunResult(false, "No test assemblies found");
        }

        var runTask = RunAllTestsAsync(assemblies);
        runTask.Wait();
        return runTask.Result;
    }

    public IEnumerable<ICoverageRunResult> CaptureCoverage(IProjectAndTests project)
    {
        var assemblies = project.GetTestAssemblies();
        if (!assemblies.Any())
        {
            return Enumerable.Empty<ICoverageRunResult>();
        }

        var captureTask = CaptureCoverageAsync(assemblies);
        captureTask.Wait();
        return captureTask.Result;
    }

    private async Task<IEnumerable<ICoverageRunResult>> CaptureCoverageAsync(IReadOnlyList<string> assemblies)
    {
        using var coverageServer = new NamedPipeCoverageServer();
        coverageServer.StartListening();

        try
        {
            // MTP doesn't support reliable per-test filtering via RPC,
            // so we run all tests once and collect coverage
            await RunAllTestsWithCoverageAsync(assemblies, coverageServer.PipeName, CancellationToken.None);

            // Give the server a moment to process any remaining messages
            await Task.Delay(100);
        }
        finally
        {
            await coverageServer.StopListeningAsync();
        }

        return coverageServer.GetCoverageResults();
    }

    private async Task RunAllTestsWithCoverageAsync(IReadOnlyList<string> assemblies, string pipeName, CancellationToken cancellationToken)
    {
        foreach (var assembly in assemblies)
        {
            if (!File.Exists(assembly))
            {
                continue;
            }

            var listener = new TcpListener(new IPEndPoint(IPAddress.Any, 0));
            listener.Start();

            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            await using var output = new MemoryStream();
            var outputPipe = PipeTarget.ToStream(output);

            var cliProcess = Cli.Wrap("dotnet")
                .WithWorkingDirectory(Path.GetDirectoryName(assembly) ?? string.Empty)
                .WithEnvironmentVariables(env =>
                {
                    // Enable coverage capture mode
                    env.Set("STRYKER_CAPTURE_COVERAGE", "true");
                    // Set the pipe name for IPC coverage reporting
                    env.Set("STRYKER_COVERAGE_PIPE", pipeName);
                    // Set mutation control to inactive (coverage mode, no mutation active)
                    env.Set("STRYKER_MUTANT_ID_CONTROL_VAR", ControlVariableName);
                    env.Set(ControlVariableName, "-1");
                    // Don't set STRYKER_CURRENT_TEST - coverage will be attributed to all tests
                })
                .WithArguments([
                    assembly,
                    "--server",
                    "--client-port",
                    port.ToString()
                ])
                .WithStandardOutputPipe(outputPipe)
                .WithStandardErrorPipe(outputPipe)
                .ExecuteAsync(cancellationToken: cancellationToken);

            var tcpClientTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            var connectionTimeout = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            var completedTask = await Task.WhenAny(cliProcess.Task, tcpClientTask, connectionTimeout);

            if (completedTask == connectionTimeout || completedTask == cliProcess.Task)
            {
                listener.Stop();
                continue;
            }

            using var tcpClient = await tcpClientTask;
            await using var stream = tcpClient.GetStream();

            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, new SystemTextJsonFormatter
            {
                JsonSerializerOptions = RpcJsonSerializerOptions.Default
            }));

            using var client = new TestingPlatformClient(rpc, tcpClient, new ProcessHandle(cliProcess, output), enableDiagnostic: false);

            await client.InitializeAsync();

            var runId = Guid.NewGuid();

            // Run all tests - no filtering
            List<TestNodeUpdate> testResults = [];
            var executeTestsResponse = await client.RunTestsAsync(runId, updates =>
            {
                testResults.AddRange(updates);
                return Task.CompletedTask;
            }, null);

            await executeTestsResponse.WaitCompletionAsync();

            await client.ExitAsync();
            listener.Stop();
        }
    }

    private async Task RunTestsWithCoverageAsync(string assembly, string pipeName, TestNode test, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(new IPEndPoint(IPAddress.Any, 0));
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var output = new MemoryStream();
        var outputPipe = PipeTarget.ToStream(output);

        var cliProcess = Cli.Wrap("dotnet")
            .WithWorkingDirectory(Path.GetDirectoryName(assembly) ?? string.Empty)
            .WithEnvironmentVariables(env =>
            {
                // Enable coverage capture mode
                env.Set("STRYKER_CAPTURE_COVERAGE", "true");
                // Set the pipe name for IPC coverage reporting
                env.Set("STRYKER_COVERAGE_PIPE", pipeName);
                // Set mutation control to inactive (coverage mode, no mutation active)
                env.Set("STRYKER_MUTANT_ID_CONTROL_VAR", ControlVariableName);
                env.Set(ControlVariableName, "-1");
                // Set the current test ID for per-test coverage tracking
                env.Set("STRYKER_CURRENT_TEST", test.Uid);
            })
            .WithArguments([
                assembly,
                "--server",
                "--client-port",
                port.ToString()
            ])
            .WithStandardOutputPipe(outputPipe)
            .WithStandardErrorPipe(outputPipe)
            .ExecuteAsync(cancellationToken: cancellationToken);

        var tcpClientTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
        var connectionTimeout = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        var completedTask = await Task.WhenAny(cliProcess.Task, tcpClientTask, connectionTimeout);

        if (completedTask == connectionTimeout || completedTask == cliProcess.Task)
        {
            listener.Stop();
            return;
        }

        using var tcpClient = await tcpClientTask;
        await using var stream = tcpClient.GetStream();

        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, new SystemTextJsonFormatter
        {
            JsonSerializerOptions = RpcJsonSerializerOptions.Default
        }));

        using var client = new TestingPlatformClient(rpc, tcpClient, new ProcessHandle(cliProcess, output), enableDiagnostic: false);

        await client.InitializeAsync();

        var runId = Guid.NewGuid();

        // Run only this specific test
        var testsToRun = new[] { test };

        List<TestNodeUpdate> testResults = [];
        var executeTestsResponse = await client.RunTestsAsync(runId, updates =>
        {
            testResults.AddRange(updates);
            return Task.CompletedTask;
        }, testsToRun);

        await executeTestsResponse.WaitCompletionAsync();

        await client.ExitAsync();
        listener.Stop();
    }

    public ITestRunResult TestMultipleMutants(IProjectAndTests project, ITimeoutValueCalculator timeoutCalc,
        IReadOnlyList<IMutant> mutants, ITestRunner.TestUpdateHandler update)
    {
        var assemblies = project.GetTestAssemblies();
        if (!assemblies.Any())
        {
            return new TestRunResult(false, "No test assemblies found");
        }

        // For MTP, we need to test each mutant individually since each test process
        // can only have one active mutation (via environment variable)
        var allExecutedTests = new List<string>();
        var allFailedTests = new List<string>();
        var allMessages = new List<string>();
        var totalDuration = TimeSpan.Zero;
        var errorMessages = new List<string>();

        foreach (var mutant in mutants)
        {
            // Determine which tests to run for this specific mutant
            var assessingTests = mutant.AssessingTests;
            HashSet<string>? testsToRun = null;
            var needAllTests = assessingTests == null || assessingTests.IsEveryTest;

            if (!needAllTests)
            {
                testsToRun = new HashSet<string>();
                foreach (var testId in assessingTests!.GetIdentifiers())
                {
                    testsToRun.Add(testId);
                }

                // If no tests cover this mutant, skip it
                if (testsToRun.Count == 0)
                {
                    continue;
                }
            }

            // Create filter function for this mutant's tests
            Func<TestNode, bool>? testFilter = needAllTests ? null : t => testsToRun!.Contains(t.Uid);

            // Test this single mutant with timeout
            var singleMutantList = new[] { mutant };
            var timeout = timeoutCalc?.DefaultTimeout ?? 30000; // Default 30s if no calculator
            using var cts = new CancellationTokenSource(timeout);
            var runTask = RunAllTestsAsync(assemblies, singleMutantList, update, testFilter, cts.Token);
            try
            {
                runTask.Wait(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Mark as timed out - use EveryTest() to signal tests timed out when running all tests
                // The mutant will be marked as Timeout by AnalyzeTestRun
                var timedOutTests = needAllTests ? TestIdentifierList.EveryTest() : new TestIdentifierList(testsToRun!);
                update?.Invoke(singleMutantList, TestIdentifierList.NoTest(), timedOutTests, timedOutTests);
                continue;
            }
            var result = runTask.Result;

            if (result is TestRunResult testResult)
            {
                allExecutedTests.AddRange(testResult.ExecutedTests.GetIdentifiers());
                allFailedTests.AddRange(testResult.FailingTests.GetIdentifiers());
                totalDuration += testResult.Duration;
                allMessages.AddRange(testResult.Messages);
                if (!string.IsNullOrWhiteSpace(testResult.ResultMessage))
                {
                    errorMessages.Add(testResult.ResultMessage);
                }
            }
        }

        var executedTests = new TestIdentifierList(allExecutedTests.Distinct());
        var failedTestIds = allFailedTests.Any()
            ? new TestIdentifierList(allFailedTests.Distinct())
            : TestIdentifierList.NoTest();

        return new TestRunResult(
            _testDescriptions.Values,
            executedTests,
            failedTestIds,
            TestIdentifierList.NoTest(),
            string.Join(Environment.NewLine, errorMessages),
            allMessages,
            totalDuration);
    }

    public void Dispose()
    {
    }

    private async Task<bool> DiscoverTestsInternalAsync(string assembly)
    {
        try
        {
            var cancellationToken = CancellationToken.None;
            var listener = new TcpListener(new IPEndPoint(IPAddress.Any, 0));
            listener.Start();

            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var cliProcess = Cli.Wrap("dotnet")
                .WithWorkingDirectory(Path.GetDirectoryName(assembly) ?? string.Empty)
                .WithArguments([
                    assembly,
                    "--server",
                    "--client-port",
                    port.ToString()
                ])
                .WithStandardOutputPipe(PipeTarget.ToDelegate(_ => { }))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(_ => { }))
                .ExecuteAsync(cancellationToken: cancellationToken);

            var tcpClientTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            var connectionTimeout = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            var completedTask = await Task.WhenAny(cliProcess.Task, tcpClientTask, connectionTimeout);

            if (completedTask == connectionTimeout || completedTask == cliProcess.Task)
            {
                listener.Stop();
                return false;
            }

            using var tcpClient = await tcpClientTask;
            await using var stream = tcpClient.GetStream();

            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, new SystemTextJsonFormatter
            {
                JsonSerializerOptions = RpcJsonSerializerOptions.Default
            }));

            using var output = new MemoryStream();
            using var client = new TestingPlatformClient(rpc, tcpClient, new ProcessHandle(cliProcess, output), enableDiagnostic: false);

            await client.InitializeAsync();

            var discoveryId = Guid.NewGuid();
            List<TestNodeUpdate> discoveredResults = [];

            var discoverTestsResponse = await client.DiscoverTestsAsync(discoveryId, updates =>
            {
                discoveredResults.AddRange(updates);
                return Task.CompletedTask;
            });

            await discoverTestsResponse.WaitCompletionAsync();

            var tests = discoveredResults
                .Where(x => x.Node.ExecutionState is "discovered")
                .Select(x => x.Node)
                .ToList();

            _testsByAssembly[assembly] = tests;

            foreach (var test in tests)
            {
                var mtpTestDescription = new MtpTestDescription(test);
                _testDescriptions[test.Uid] = mtpTestDescription;
                _testSet.RegisterTest(mtpTestDescription.Description);
            }

            await client.ExitAsync();
            listener.Stop();

            return tests.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<ITestRunResult> RunAllTestsAsync(IReadOnlyList<string> assemblies, IReadOnlyList<IMutant>? mutants = null, ITestRunner.TestUpdateHandler? update = null, Func<TestNode, bool>? testFilter = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var allExecutedTests = new List<string>();
            var allFailedTests = new List<string>();
            var allMessages = new List<string>();
            var totalDuration = TimeSpan.Zero;
            var errorMessages = new List<string>();
            var anyEveryTest = false;

            foreach (var assembly in assemblies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(assembly))
                {
                    continue;
                }

                var testResults = await RunTestsInternalAsync(assembly, cancellationToken, testFilter, mutants, update);

                if (testResults is TestRunResult result)
                {
                    // Track if any sub-result indicates "every test" was run
                    if (result.ExecutedTests.IsEveryTest)
                    {
                        anyEveryTest = true;
                    }
                    else
                    {
                        allExecutedTests.AddRange(result.ExecutedTests.GetIdentifiers());
                    }
                    allFailedTests.AddRange(result.FailingTests.GetIdentifiers());
                    totalDuration += result.Duration;
                    allMessages.AddRange(result.Messages);
                    if (!string.IsNullOrWhiteSpace(result.ResultMessage))
                    {
                        errorMessages.Add(result.ResultMessage);
                    }
                }
            }

            // If any assembly ran all tests without filtering, signal EveryTest() for the aggregate
            ITestIdentifiers executedTests = anyEveryTest
                ? TestIdentifierList.EveryTest()
                : new TestIdentifierList(allExecutedTests);
            var failedTestIds = allFailedTests.Any()
                ? new TestIdentifierList(allFailedTests)
                : TestIdentifierList.NoTest();

            return new TestRunResult(
                _testDescriptions.Values,
                executedTests,
                failedTestIds,
                TestIdentifierList.NoTest(),
                string.Join(Environment.NewLine, errorMessages),
                allMessages,
                totalDuration);
        }
        catch (Exception ex)
        {
            return new TestRunResult(false, ex.Message);
        }
    }

    private async Task<ITestRunResult> RunTestsInternalAsync(string assembly, CancellationToken cancellationToken,
        Func<TestNode, bool>? testUidFilter, IReadOnlyList<IMutant>? mutants = null, ITestRunner.TestUpdateHandler? update = null)
    {
        var startTime = DateTime.UtcNow;
        var listener = new TcpListener(new IPEndPoint(IPAddress.Any, 0));
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var output = new MemoryStream();
        var outputPipe = PipeTarget.ToStream(output);

        // Determine which mutation should be active
        // For mutation testing, we need to activate the specific mutant being tested
        var activeMutantId = mutants is { Count: > 0 } ? mutants[0].Id : -1;

        var cliProcess = Cli.Wrap("dotnet")
            .WithWorkingDirectory(Path.GetDirectoryName(assembly) ?? string.Empty)
            .WithEnvironmentVariables(env =>
            {
                // Set the indirection variable that tells MutantControl where to find the active mutation ID
                env.Set("STRYKER_MUTANT_ID_CONTROL_VAR", ControlVariableName);
                // Set the actual mutation ID
                env.Set(ControlVariableName, activeMutantId.ToString());
            })
            .WithArguments([
                assembly,
                "--server",
                "--client-port",
                port.ToString()
            ])
            .WithStandardOutputPipe(outputPipe)
            .WithStandardErrorPipe(outputPipe)
            .ExecuteAsync(cancellationToken: cancellationToken);

        var tcpClientTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
        var connectionTimeout = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        var completedTask = await Task.WhenAny(cliProcess.Task, tcpClientTask, connectionTimeout);

        if (completedTask == connectionTimeout)
        {
            listener.Stop();
            return new TestRunResult(false, "Timeout waiting for test connection");
        }

        if (completedTask == cliProcess.Task)
        {
            listener.Stop();
            var result = await cliProcess.Task;
            return new TestRunResult(false, $"Test process exited with code {result.ExitCode}");
        }

        using var tcpClient = await tcpClientTask;
        await using var stream = tcpClient.GetStream();

        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, new SystemTextJsonFormatter
        {
            JsonSerializerOptions = RpcJsonSerializerOptions.Default
        }));

        using var client = new TestingPlatformClient(rpc, tcpClient, new ProcessHandle(cliProcess, output), enableDiagnostic: false);

        await client.InitializeAsync();

        var runId = Guid.NewGuid();
        List<TestNodeUpdate> testResults = [];

        var testsToRun = _testsByAssembly.TryGetValue(assembly, out var tests)
            ? tests.Where(t => testUidFilter == null || testUidFilter(t)).ToArray()
            : null;

        var executeTestsResponse = await client.RunTestsAsync(runId, updates =>
        {
            testResults.AddRange(updates);
            return Task.CompletedTask;
        }, testsToRun);

        // Wait for completion with timeout support
        var completionTask = executeTestsResponse.WaitCompletionAsync();
        var timeoutTask = Task.Delay(Timeout.Infinite, cancellationToken);

        var completed = await Task.WhenAny(completionTask, timeoutTask);
        if (completed == timeoutTask)
        {
            // Timeout - kill the test process
            client.Dispose();
            listener.Stop();
            cancellationToken.ThrowIfCancellationRequested();
        }

        await client.ExitAsync();
        listener.Stop();

        var duration = DateTime.UtcNow - startTime;
        var finishedTests = testResults.Where(x => x.Node.ExecutionState is not "in-progress").ToList();
        var failedTests = finishedTests.Where(x => x.Node.ExecutionState is "failed").Select(x => x.Node.Uid).ToList();

        foreach (var testResult in finishedTests)
        {
            if (_testDescriptions.TryGetValue(testResult.Node.Uid, out var testDescription))
            {
                testDescription.RegisterInitialTestResult(new MtpTestResult(duration));
            }
        }

        var errorMessages = string.Join(Environment.NewLine,
            finishedTests.Where(x => x.Node.ExecutionState is "failed")
                .Select(x => $"{x.Node.DisplayName}{Environment.NewLine}{Environment.NewLine}Test failed"));

        var messages = finishedTests.Select(x =>
            $"{x.Node.DisplayName}{Environment.NewLine}{Environment.NewLine}State: {x.Node.ExecutionState}");

        // If testing mutants without a filter, we ran all tests - use EveryTest() to signal this
        // This is important for mutants with AssessingTests=EveryTest() to be marked as Survived
        // For initial test runs (mutants == null), we need specific test IDs for validation
        ITestIdentifiers executedTests = (mutants != null && testUidFilter == null)
            ? TestIdentifierList.EveryTest()
            : new TestIdentifierList(finishedTests.Select(x => x.Node.Uid));
        var failedTestIds = failedTests.Any()
            ? new TestIdentifierList(failedTests)
            : TestIdentifierList.NoTest();

        if (update != null && mutants != null)
        {
            update.Invoke(mutants, failedTestIds, executedTests, TestIdentifierList.NoTest());
        }

        return new TestRunResult(
            _testDescriptions.Values,
            executedTests,
            failedTestIds,
            TestIdentifierList.NoTest(),
            errorMessages,
            messages,
            duration);
    }
}
