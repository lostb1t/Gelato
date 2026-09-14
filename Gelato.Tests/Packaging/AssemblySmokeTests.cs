namespace Gelato.Tests.Packaging;

public class AssemblySmokeTests
{
    [Fact]
    public void GelatoAssembly_LoadsAndIsNamedGelato()
    {
        var asm = typeof(GelatoPlugin).Assembly;

        Assert.Equal("Gelato", asm.GetName().Name);
        Assert.True(File.Exists(asm.Location), $"assembly not on disk: {asm.Location}");
    }
}
