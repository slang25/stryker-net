using System.Text.Json.Serialization;

namespace Stryker.TestRunner.Coverage;

/// <summary>
/// Base class for coverage messages sent from the test process to Stryker.
/// </summary>
public abstract class CoverageMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}

/// <summary>
/// Message sent when a test starts executing.
/// </summary>
public class TestStartMessage : CoverageMessage
{
    public const string MessageType = "test_start";

    [JsonPropertyName("testId")]
    public string TestId { get; init; } = string.Empty;
}

/// <summary>
/// Message sent when a test finishes executing, including coverage data.
/// </summary>
public class TestEndMessage : CoverageMessage
{
    public const string MessageType = "test_end";

    [JsonPropertyName("testId")]
    public string TestId { get; init; } = string.Empty;

    [JsonPropertyName("coveredMutants")]
    public int[] CoveredMutants { get; init; } = [];

    [JsonPropertyName("staticMutants")]
    public int[] StaticMutants { get; init; } = [];
}

/// <summary>
/// Message sent when a mutation is covered (for real-time streaming).
/// </summary>
public class MutantCoveredMessage : CoverageMessage
{
    public const string MessageType = "mutant_covered";

    [JsonPropertyName("testId")]
    public string TestId { get; init; } = string.Empty;

    [JsonPropertyName("mutantId")]
    public int MutantId { get; init; }

    [JsonPropertyName("isStatic")]
    public bool IsStatic { get; init; }
}

/// <summary>
/// Message sent when coverage capture is complete for the session.
/// </summary>
public class SessionEndMessage : CoverageMessage
{
    public const string MessageType = "session_end";
}
