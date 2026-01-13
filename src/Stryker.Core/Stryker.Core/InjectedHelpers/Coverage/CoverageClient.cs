namespace Stryker
{
    /// <summary>
    /// Client that connects to Stryker's coverage server via named pipes.
    /// This class is injected into test assemblies to enable coverage reporting for MTP tests.
    /// </summary>
    internal static class CoverageClient
    {
        private static System.IO.Pipes.NamedPipeClientStream _pipe;
        private static System.IO.StreamWriter _writer;
        private static string _pipeName;
        private static bool _initialized;
        private static bool _connected;
        private static readonly object _lock = new object();
        private static string _currentTestId;

        /// <summary>
        /// Initializes the coverage client by reading the pipe name from environment variable
        /// and attempting to connect to the server.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            lock (_lock)
            {
                if (_initialized)
                {
                    return;
                }

                _initialized = true;
                _pipeName = System.Environment.GetEnvironmentVariable("STRYKER_COVERAGE_PIPE");

                if (string.IsNullOrEmpty(_pipeName))
                {
                    return;
                }

                try
                {
                    _pipe = new System.IO.Pipes.NamedPipeClientStream(".", _pipeName, System.IO.Pipes.PipeDirection.Out);
                    _pipe.Connect(5000); // 5 second timeout
                    _writer = new System.IO.StreamWriter(_pipe) { AutoFlush = true };
                    _connected = true;
                }
                catch
                {
                    // Connection failed, coverage will be unavailable
                    _connected = false;
                }
            }
        }

        /// <summary>
        /// Reports that a test has started executing.
        /// </summary>
        public static void TestStart(string testId)
        {
            if (!_connected)
            {
                return;
            }

            _currentTestId = testId;
            SendMessage("{\"type\":\"test_start\",\"testId\":\"" + EscapeJson(testId) + "\"}");
        }

        /// <summary>
        /// Reports that a test has finished executing with the collected coverage data.
        /// </summary>
        public static void TestEnd(string testId, System.Collections.Generic.IList<int> coveredMutants, System.Collections.Generic.IList<int> staticMutants)
        {
            if (!_connected)
            {
                return;
            }

            var covered = coveredMutants != null ? string.Join(",", coveredMutants) : "";
            var statics = staticMutants != null ? string.Join(",", staticMutants) : "";

            SendMessage("{\"type\":\"test_end\",\"testId\":\"" + EscapeJson(testId) + "\",\"coveredMutants\":[" + covered + "],\"staticMutants\":[" + statics + "]}");
            _currentTestId = null;
        }

        /// <summary>
        /// Reports that a mutant was covered (for real-time streaming).
        /// </summary>
        public static void ReportMutantCovered(int mutantId, bool isStatic)
        {
            if (!_connected)
            {
                return;
            }

            SendMessage("{\"type\":\"mutant_covered\",\"mutantId\":" + mutantId + ",\"isStatic\":" + (isStatic ? "true" : "false") + "}");
        }

        /// <summary>
        /// Reports that the coverage session has ended.
        /// </summary>
        public static void SessionEnd()
        {
            if (!_connected)
            {
                return;
            }

            SendMessage("{\"type\":\"session_end\"}");
            Close();
        }

        /// <summary>
        /// Closes the connection to the coverage server.
        /// </summary>
        public static void Close()
        {
            lock (_lock)
            {
                if (_writer != null)
                {
                    try
                    {
                        _writer.Dispose();
                    }
                    catch { }
                    _writer = null;
                }

                if (_pipe != null)
                {
                    try
                    {
                        _pipe.Dispose();
                    }
                    catch { }
                    _pipe = null;
                }

                _connected = false;
            }
        }

        private static void SendMessage(string json)
        {
            lock (_lock)
            {
                if (_writer == null || !_connected)
                {
                    return;
                }

                try
                {
                    _writer.WriteLine(json);
                }
                catch
                {
                    // Connection lost
                    _connected = false;
                }
            }
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            var sb = new System.Text.StringBuilder();
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
