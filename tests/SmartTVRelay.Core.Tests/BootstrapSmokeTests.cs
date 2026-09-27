using System.Reflection;
using Xunit;

namespace SmartTVRelay.Core.Tests;

public sealed class BootstrapSmokeTests
{
    [Fact]
    public void CoreAssemblyLoads()
    {
        var assembly = Assembly.Load("SmartTVRelay.Core");

        Assert.Equal("SmartTVRelay.Core", assembly.GetName().Name);
    }
}
