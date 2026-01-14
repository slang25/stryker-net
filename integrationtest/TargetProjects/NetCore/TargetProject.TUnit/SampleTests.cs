using TargetProject.Constructs;
using TargetProject.Defects;
using TargetProject.StrykerFeatures;

namespace TargetProject.TUnit;

public class SampleTests
{
    [Test]
    public async Task ExampleChain_ReturnsExpectedChar()
    {
        var c = new CSharp1().ExampleChain();
        await Assert.That(c).IsEqualTo('S');
    }

    [Test]
    public async Task Employee_Methods_DoNotThrow()
    {
        var e = new CSharp2.Employee();
        e.DoWork();
        e.GoToLunch();
        await Assert.That(true).IsTrue(); // If the methods threw, the test would fail.
    }

    [Test]
    public async Task WhileTrue_Loop_ReturnsTrueWhenStopTrue()
    {
        var result = WhileTrue.Loop(true);
        await Assert.That(result).IsTrue();
    }

    // Tests for KilledMutants class - these should kill mutants on >= boundary
    [Test]
    public async Task IsExpired_WhenAge29_ReturnsNo()
    {
        var km = new KilledMutants { Age = 29 };
        var result = km.IsExpired();
        await Assert.That(result).IsEqualTo("No");
    }

    [Test]
    public async Task IsExpired_WhenAge30_ReturnsYes()
    {
        var km = new KilledMutants { Age = 30 };
        var result = km.IsExpired();
        await Assert.That(result).IsEqualTo("Yes");
    }

    [Test]
    public async Task IsExpired_WhenAge31_ReturnsYes()
    {
        var km = new KilledMutants { Age = 31 };
        var result = km.IsExpired();
        await Assert.That(result).IsEqualTo("Yes");
    }

    [Test]
    public async Task IsExpiredBool_WhenAge29_ReturnsFalse()
    {
        var km = new KilledMutants { Age = 29 };
        var result = km.IsExpiredBool();
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsExpiredBool_WhenAge30_ReturnsTrue()
    {
        var km = new KilledMutants { Age = 30 };
        var result = km.IsExpiredBool();
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsExpiredBool_WhenAge31_ReturnsTrue()
    {
        var km = new KilledMutants { Age = 31 };
        var result = km.IsExpiredBool();
        await Assert.That(result).IsTrue();
    }
}
