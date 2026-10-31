using TransFlux.Domain.Common;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;

namespace TransFlux.Domain.Tests;

public class BuiltInPresetsTests
{
    [Fact]
    public void All_ContainsFourUniqueBuiltInPresets()
    {
        var all = BuiltInPresets.All;
        Assert.Equal(4, all.Count);
        Assert.All(all, p => Assert.True(p.BuiltIn));
        Assert.Equal(all.Count, all.Select(p => p.PresetId).Distinct().Count());
    }

    [Fact]
    public void Get_ReturnsMatchingPreset()
    {
        var p = BuiltInPresets.Get(BuiltInPresets.ExtractAudioId);
        Assert.Equal(ConversionMode.AudioExtract, p.Template.Mode);
        Assert.Equal(ContainerFormat.M4a, p.Template.Container);
        Assert.Equal(AudioCodec.Aac, p.Template.AudioCodec);
    }

    [Fact]
    public void Get_UnknownId_Throws()
    {
        Assert.Throws<DomainException>(() => BuiltInPresets.Get(Guid.NewGuid()));
    }
}
