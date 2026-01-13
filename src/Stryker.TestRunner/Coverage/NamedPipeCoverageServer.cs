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
    private NamedPipeServerStream? _serverStream;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private bool _disposed;

    /// <summary>
    /// Gets the unique pipe name for this server instance.
    /// Test processes use this name to connect and send coverage data.
    /// </summary>
    public string PipeName { get; } = $"stryker_coverage_{Guid.NewGuid():N}";

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
    }

    /// <summary>
    /// Gets the collected coverage results.
    /// </summary>
    public IEnumerable<ICoverageRunResult> GetCoverageResults()
    {
        return _coverageByTest.Select(kvp =>
            CoverageRunResult.Create(
                kvp.Key,
                CoverageConfidence.Normal,
                kvp.Value.CoveredMutants,
                kvp.Value.StaticMutants,
                kvp.Value.LeakedMutants.Concat(_leakedMutants)));
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _serverStream = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await _serverStream.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(_serverStream, Encoding.UTF8, leaveOpen: true);

                while (!ct.IsCancellationRequested && _serverStream.IsConnected)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    ProcessMessage(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Connection closed, may reconnect
            }
            finally
            {
                _serverStream?.Dispose();
                _serverStream = null;
            }
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

        // Real-time coverage streaming - for now just track leaked mutants
        // This could be enhanced to track per-test coverage in real-time
        _leakedMutants.Add(message.MutantId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _serverStream?.Dispose();
        _cts?.Dispose();
    }

    private class TestCoverageData
    {
        public List<int> CoveredMutants { get; } = [];
        public List<int> StaticMutants { get; } = [];
        public List<int> LeakedMutants { get; } = [];
    }
}
