using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.Results;

namespace Stryker.TestRunner.Coverage;

/// <summary>
/// Server that listens for coverage data from test processes via named pipes.
/// This enables MTP tests to report mutation coverage back to Stryker.
/// </summary>
public sealed class NamedPipeCoverageServer : IDisposable
{
    private readonly ConcurrentDictionary<string, TestCoverageData> _coverageByTest = new();
    private readonly ConcurrentBag<int> _leakedMutants = new();
    private readonly List<Task> _connectionTasks = new();
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private bool _disposed;
    private int _connectionCount;

    /// <summary>
    /// Gets the unique pipe name for this server instance.
    /// Test processes use this name to connect and send coverage data.
    /// Note: Pipe name must be short to fit Unix domain socket path limits (104 chars).
    /// </summary>
    public string PipeName { get; } = $"scov_{Guid.NewGuid().ToString("N")[..12]}";

    /// <summary>
    /// Starts listening for coverage data from test processes.
    /// </summary>
    public void StartListening()
    {
        if (_listenerTask != null)
        {
            throw new InvalidOperationException("Server is already listening");
        }

        _cts = new CancellationTokenSource();
        _listenerTask = ListenAsync(_cts.Token);
    }

    /// <summary>
    /// Stops listening and waits for the listener to complete.
    /// </summary>
    public async Task StopListeningAsync()
    {
        if (_cts == null || _listenerTask == null)
        {
            return;
        }

        _cts.Cancel();

        try
        {
            await _listenerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelling
        }

        // Wait for all connection tasks to complete
        Task[] tasks;
        lock (_connectionTasks)
        {
            tasks = _connectionTasks.ToArray();
        }

        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling
            }
        }
    }

    /// <summary>
    /// Gets the collected coverage results.
    /// </summary>
    public IEnumerable<ICoverageRunResult> GetCoverageResults()
    {
        // If we have per-test coverage, return it
        if (_coverageByTest.Any())
        {
            return _coverageByTest.Select(kvp =>
                CoverageRunResult.Create(
                    kvp.Key,
                    CoverageConfidence.Normal,
                    kvp.Value.CoveredMutants,
                    kvp.Value.StaticMutants,
                    kvp.Value.LeakedMutants.Concat(_leakedMutants)));
        }

        // If no per-test coverage but we have leaked mutants, return a single result
        // This happens when MTP runs all tests without per-test tracking
        if (_leakedMutants.Any())
        {
            var coveredMutants = _leakedMutants.Distinct().ToList();

            // Return a single "all tests" coverage result
            // The special "_AllTests_" marker tells CoverageAnalyser to test covered mutants
            // against all tests since we don't have per-test granularity
            return new[]
            {
                CoverageRunResult.Create(
                    "_AllTests_",
                    CoverageConfidence.Dubious,
                    coveredMutants,
                    new List<int>(),  // No static tracking without per-test coverage
                    new List<int>())
            };
        }

        return Enumerable.Empty<ICoverageRunResult>();
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? serverStream = null;
            try
            {
                // Allow multiple server instances for concurrent test processes
                serverStream = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await serverStream.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var connectionId = Interlocked.Increment(ref _connectionCount);

                // Handle this connection in a separate task so we can accept more connections
                var connectionTask = HandleConnectionAsync(serverStream, connectionId, ct);
                lock (_connectionTasks)
                {
                    _connectionTasks.Add(connectionTask);
                }
            }
            catch (OperationCanceledException)
            {
                serverStream?.Dispose();
                break;
            }
            catch (IOException)
            {
                serverStream?.Dispose();
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream serverStream, int connectionId, CancellationToken ct)
    {
        try
        {
            using (serverStream)
            using (var reader = new StreamReader(serverStream, Encoding.UTF8, leaveOpen: true))
            {
                while (!ct.IsCancellationRequested && serverStream.IsConnected)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    ProcessMessage(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelling
        }
        catch (IOException)
        {
            // Connection lost or error - ignore
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case TestStartMessage.MessageType:
                    HandleTestStart(JsonSerializer.Deserialize<TestStartMessage>(json));
                    break;

                case TestEndMessage.MessageType:
                    HandleTestEnd(JsonSerializer.Deserialize<TestEndMessage>(json));
                    break;

                case MutantCoveredMessage.MessageType:
                    HandleMutantCovered(JsonSerializer.Deserialize<MutantCoveredMessage>(json));
                    break;

                case SessionEndMessage.MessageType:
                    // Session complete, stop listening
                    _cts?.Cancel();
                    break;
            }
        }
        catch (JsonException)
        {
            // Invalid message, ignore
        }
    }

    private void HandleTestStart(TestStartMessage? message)
    {
        if (message == null)
        {
            return;
        }

        // Initialize coverage data for this test
        _coverageByTest.TryAdd(message.TestId, new TestCoverageData());
    }

    private void HandleTestEnd(TestEndMessage? message)
    {
        if (message == null)
        {
            return;
        }

        var coverageData = _coverageByTest.GetOrAdd(message.TestId, _ => new TestCoverageData());
        coverageData.CoveredMutants.AddRange(message.CoveredMutants);
        coverageData.StaticMutants.AddRange(message.StaticMutants);
    }

    private void HandleMutantCovered(MutantCoveredMessage? message)
    {
        if (message == null)
        {
            return;
        }

        // If we have a test ID, track coverage per test
        if (!string.IsNullOrEmpty(message.TestId))
        {
            var coverageData = _coverageByTest.GetOrAdd(message.TestId, _ => new TestCoverageData());
            if (message.IsStatic)
            {
                coverageData.StaticMutants.Add(message.MutantId);
            }
            else
            {
                coverageData.CoveredMutants.Add(message.MutantId);
            }
        }
        else
        {
            // No test ID - track as leaked mutant
            _leakedMutants.Add(message.MutantId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private class TestCoverageData
    {
        public List<int> CoveredMutants { get; } = [];
        public List<int> StaticMutants { get; } = [];
        public List<int> LeakedMutants { get; } = [];
    }
}
