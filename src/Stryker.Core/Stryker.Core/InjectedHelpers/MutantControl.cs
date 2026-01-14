namespace Stryker
{

    public static class MutantControl
    {
        private static System.Collections.Generic.List<int> _coveredMutants = new System.Collections.Generic.List<int>();
        private static System.Collections.Generic.List<int> _coveredStaticMutants = new System.Collections.Generic.List<int>();
        private static string envName = string.Empty;
        private static System.Object _coverageLock = new System.Object();
        private static System.Object _initLock = new System.Object();
        private static bool _coverageModeChecked;

        // this attribute will be set by the Stryker Data Collector before each test
        // For MTP, this can also be set via STRYKER_CAPTURE_COVERAGE environment variable
        public static bool CaptureCoverage;
        public static int ActiveMutant = -2;
        public const int ActiveMutantNotInitValue = -2;

        public static void InitCoverage()
        {
            ResetCoverage();
            // Initialize IPC coverage client for MTP support (no-op if pipe not configured)
            CoverageClient.Initialize();
        }

        public static void ResetCoverage()
        {
            _coveredMutants = new System.Collections.Generic.List<int>();
            _coveredStaticMutants = new System.Collections.Generic.List<int>();
        }

        public static System.Collections.Generic.IList<int>[] GetCoverageData()
        {
            System.Collections.Generic.IList<int>[] result = new System.Collections.Generic.IList<int>[] { _coveredMutants, _coveredStaticMutants };
            ResetCoverage();
            return result;
        }

        private static void CurrentDomain_ProcessExit(object sender, System.EventArgs e)
        {
            System.GC.KeepAlive(_coveredMutants);
            System.GC.KeepAlive(_coveredStaticMutants);
        }

        // check with: Stryker.MutantControl.IsActive(ID)
        public static bool IsActive(int id)
        {
            // Check for coverage mode via environment variable (MTP support)
            // Use double-checked locking to handle concurrent calls from multiple threads
            if (!_coverageModeChecked)
            {
                lock (_initLock)
                {
                    if (!_coverageModeChecked)
                    {
                        var coverageEnv = System.Environment.GetEnvironmentVariable("STRYKER_CAPTURE_COVERAGE");
                        if (!string.IsNullOrEmpty(coverageEnv) && coverageEnv.Equals("true", System.StringComparison.OrdinalIgnoreCase))
                        {
                            // Initialize first, THEN enable coverage mode to prevent race condition
                            // where other threads see CaptureCoverage=true before connection is established
                            InitCoverage();
                            CaptureCoverage = true;
                        }
                        _coverageModeChecked = true;
                    }
                }
            }

            if (CaptureCoverage)
            {
                RegisterCoverage(id);
                return false;
            }
            if (ActiveMutant == ActiveMutantNotInitValue)
            {
#pragma warning disable CS8600
                // get the environment variable storing the mutation id
                string environmentVariableName = System.Environment.GetEnvironmentVariable("STRYKER_MUTANT_ID_CONTROL_VAR");
                if (environmentVariableName != null)
                {
                    string environmentVariable = System.Environment.GetEnvironmentVariable(environmentVariableName);
                    if (string.IsNullOrEmpty(environmentVariable))
                    {
                        ActiveMutant = -1;
                    }
                    else
                    {
                        ActiveMutant = int.Parse(environmentVariable);
                    }
                }
                else
                {
                    ActiveMutant = -1;
                }
            }

            return id == ActiveMutant;
        }

        private static void RegisterCoverage(int id)
        {
            var isStatic = MutantContext.InStatic();
            lock (_coverageLock)
            {
                if (!_coveredMutants.Contains(id))
                {
                    _coveredMutants.Add(id);
                }
                if (isStatic && !_coveredStaticMutants.Contains(id))
                {
                    _coveredStaticMutants.Add(id);
                }
            }
            // Report coverage via IPC for MTP support (no-op if not connected)
            CoverageClient.ReportMutantCovered(id, isStatic);
        }
    }
}
