namespace PANiXiDA.Core.Template.UnitTests;

public sealed class TemplateSmokeTests
{
    [Fact(DisplayName = "Unit test assembly uses the template name")]
    public void Assembly_WhenLoaded_UsesTemplateName()
    {
        var assemblyName = typeof(TemplateSmokeTests).Assembly.GetName().Name;

        assemblyName.ShouldBe("PANiXiDA.Core.Template.UnitTests");
    }
}
