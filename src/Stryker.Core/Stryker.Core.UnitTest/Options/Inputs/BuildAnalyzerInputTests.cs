using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using Stryker.Abstractions.Exceptions;
using Stryker.Abstractions.Options;
using Stryker.Configuration.Options.Inputs;

namespace Stryker.Core.UnitTest.Options.Inputs;

[TestClass]
public class BuildAnalyzerInputTests : TestBase
{
    [TestMethod]
    public void ShouldHaveHelpText()
    {
        var target = new BuildAnalyzerInput();
        target.HelpText.ShouldBe("Specify which backend to use for analyzing the project under test. | default: 'buildalyzer' | allowed: buildalyzer, msbuildworkspace");
    }

    [TestMethod]
    public void ShouldDefaultToBuildalyzer()
    {
        var target = new BuildAnalyzerInput { SuppliedInput = null };

        var result = target.Validate();

        result.ShouldBe(BuildAnalyzer.Buildalyzer);
    }

    [TestMethod]
    [DataRow("buildalyzer", BuildAnalyzer.Buildalyzer)]
    [DataRow("Buildalyzer", BuildAnalyzer.Buildalyzer)]
    [DataRow("BUILDALYZER", BuildAnalyzer.Buildalyzer)]
    [DataRow("msbuildworkspace", BuildAnalyzer.MSBuildWorkspace)]
    [DataRow("MSBuildWorkspace", BuildAnalyzer.MSBuildWorkspace)]
    [DataRow("MSBUILDWORKSPACE", BuildAnalyzer.MSBuildWorkspace)]
    public void ShouldParseValidInputs(string input, BuildAnalyzer expected)
    {
        var target = new BuildAnalyzerInput { SuppliedInput = input };

        var result = target.Validate();

        result.ShouldBe(expected);
    }

    [TestMethod]
    public void ShouldThrowOnInvalidInput()
    {
        var target = new BuildAnalyzerInput { SuppliedInput = "gibberish" };

        var ex = Should.Throw<InputException>(() => target.Validate());

        ex.Message.ShouldContain("Invalid build analyzer 'gibberish'");
    }
}
