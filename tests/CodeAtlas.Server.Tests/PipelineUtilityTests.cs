namespace CodeAtlas.Server.Tests;

public class PipelineUtilityTests
{
    [Theory]
    [InlineData("Acme.Tests/OrdersTests.cs", "Acme.Tests.OrdersTests", true)]
    [InlineData("src/__tests__/a.ts", "src/__tests__/a.ts:f", true)]
    [InlineData("src/app.spec.ts", "src/app.spec.ts:f", true)]
    [InlineData("Acme.Api/OrdersController.cs", "Acme.Api.OrdersController", false)]
    [InlineData("Acme.Api/Testimony.cs", "Acme.Api.Testimony", false)]
    public void IsTest_MatchesWebviewRule(string path, string qname, bool expected)
    {
        Assert.Equal(expected, PipelineBuilder.IsTest(path, qname));
    }
}
