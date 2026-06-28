using System.Collections.Generic;
using Stryker.Abstractions.Exceptions;
using Stryker.Abstractions.Options;

namespace Stryker.Configuration.Options.Inputs;

public class BuildAnalyzerInput : Input<string>
{
    public override string Default => "buildalyzer";
    protected override string Description => "Specify which backend to use for analyzing the project under test.";
    protected override IEnumerable<string> AllowedOptions => new[] { "buildalyzer", "msbuildworkspace" };

    public BuildAnalyzer Validate()
    {
        if (SuppliedInput is null)
        {
            return BuildAnalyzer.Buildalyzer;
        }

        return SuppliedInput.ToLowerInvariant() switch
        {
            "buildalyzer" => BuildAnalyzer.Buildalyzer,
            "msbuildworkspace" => BuildAnalyzer.MSBuildWorkspace,
            _ => throw new InputException($"Invalid build analyzer '{SuppliedInput}'. Valid options are: {string.Join(", ", AllowedOptions)}")
        };
    }
}
