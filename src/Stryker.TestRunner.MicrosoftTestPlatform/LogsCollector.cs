using System.Collections.Concurrent;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

public class LogsCollector : ConcurrentBag<TestingPlatformClient.Log>;
