using TransFlux.Application.Contracts;
using TransFlux.Engine.FFmpeg.Capabilities;

namespace TransFlux.Engine.IntegrationTests;

public class CapabilitiesTests
{
    [Fact]
    public void GetIdentity_ReportsCoreHashAndVersion()
    {
        var caps = new FfmpegEngineCapabilities(TestSupport.Core);
        var identity = caps.GetIdentity();
        Assert.Equal(TestSupport.Core.CoreHash, identity.CoreHash);
        Assert.Equal(TestSupport.Core.FfmpegPath, identity.FfmpegPath);
        Assert.False(string.IsNullOrWhiteSpace(identity.FfmpegVersion));
        Assert.False(string.IsNullOrWhiteSpace(identity.BuildConfiguration));
        Assert.Contains("--enable-gpl", identity.BuildConfiguration);
    }

    [Fact]
    public async Task QueryAsync_ReportsEncodersFiltersAndPixelFormats()
    {
        var caps = new FfmpegEngineCapabilities(TestSupport.Core);
        var info = await caps.QueryAsync(CancellationToken.None);
        Assert.Contains("libx264", info.Encoders);
        Assert.Contains("aac", info.Encoders);
        Assert.Contains("h264", info.Decoders);
        Assert.Contains("yuv420p", info.PixelFormats);
        Assert.NotEmpty(info.Filters);
        Assert.NotEmpty(info.Hwaccels);
    }

    [Fact]
    public async Task ValidateEncoder_Libx264_Succeeds()
    {
        var caps = new FfmpegEngineCapabilities(TestSupport.Core);
        var result = await caps.ValidateEncoderAsync(new HardwareValidationRequest
        {
            EncoderName = "libx264",
            Width = 160,
            Height = 120,
            PixelFormat = "yuv420p",
            Timeout = TimeSpan.FromSeconds(15),
        }, CancellationToken.None);
        Assert.True(result.Success, result.FailureReason);
    }

    [Fact]
    public async Task ValidateEncoder_UnknownEncoder_FailsWithReason()
    {
        var caps = new FfmpegEngineCapabilities(TestSupport.Core);
        var result = await caps.ValidateEncoderAsync(new HardwareValidationRequest
        {
            EncoderName = "definitely_not_a_real_encoder",
            Width = 160,
            Height = 120,
            Timeout = TimeSpan.FromSeconds(15),
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }
}
