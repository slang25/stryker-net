using System.Collections.Concurrent;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

public class TelemetryCollector : ConcurrentBag<TelemetryPayload>;
