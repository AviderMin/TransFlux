using TransFlux.Domain.Plans;
using TransFlux.Domain.Presets;

namespace TransFlux.Domain.Rules;

public static class BuiltInPresets
{
    public static readonly Guid GeneralVideoBalancedId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid SmallerSizeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ExtractAudioId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid RemuxId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public static IReadOnlyList<Preset> All => new[]
    {
        new Preset { PresetId = GeneralVideoBalancedId, Name = "通用视频 · 均衡", BuiltIn = true,
            Template = new PresetRequestTemplate { Mode = ConversionMode.VideoTranscode, Container = ContainerFormat.Mp4,
                Quality = QualityTier.Balanced, Speed = SpeedTier.Balanced } },
        new Preset { PresetId = SmallerSizeId, Name = "节省空间", BuiltIn = true,
            Template = new PresetRequestTemplate { Mode = ConversionMode.VideoTranscode, Container = ContainerFormat.Mp4,
                Quality = QualityTier.SmallerSize, Speed = SpeedTier.Balanced, ResizeMode = ResizeMode.Max1080p } },
        new Preset { PresetId = ExtractAudioId, Name = "提取音频", BuiltIn = true,
            Template = new PresetRequestTemplate { Mode = ConversionMode.AudioExtract, Container = ContainerFormat.M4a,
                AudioCodec = AudioCodec.Aac, AudioBitRateKbps = 192 } },
        new Preset { PresetId = RemuxId, Name = "只换封装", BuiltIn = true,
            Template = new PresetRequestTemplate { Mode = ConversionMode.Remux, Container = ContainerFormat.Mkv } },
    };

    public static Preset Get(Guid id) => All.FirstOrDefault(p => p.PresetId == id)
        ?? throw new Common.DomainException($"Unknown built-in preset {id}.");
}
