using System.Globalization;

namespace TransFlux.Engine.FFmpeg.Capabilities;

/// <summary>Shared device binding for trial and execution. DXGI LUIDs remain identity metadata.</summary>
public static class HardwareEncoding
{
    public static bool IsSupported(string encoder) => encoder is "h264_nvenc" or "h264_qsv" or "h264_amf";

    public static void AddDevice(List<string> args, string encoder, int? adapterIndex)
    {
        if (adapterIndex is null) return;
        if (adapterIndex < 0 || !IsSupported(encoder)) throw new ArgumentException("Invalid hardware encoder/device binding.");
        args.Add("-init_hw_device");
        args.Add("d3d11va=tfbase:" + adapterIndex.Value.ToString(CultureInfo.InvariantCulture));
        if (encoder == "h264_qsv")
        {
            args.Add("-init_hw_device"); args.Add("qsv=tfencode@tfbase");
        }
        args.Add("-filter_hw_device"); args.Add(encoder == "h264_qsv" ? "tfencode" : "tfbase");
    }

    public static string UploadFilter(string encoder) => encoder == "h264_qsv"
        ? "format=nv12,hwupload=extra_hw_frames=64" : "format=nv12,hwupload";

    public static string FailureSummary(string detail)
    {
        var lower = detail.ToLowerInvariant();
        if (lower.Contains("nvenc api version") || lower.Contains("minimum required nvidia driver"))
            return "媒体核心要求的 NVIDIA 编码 API 高于当前驱动，请使用兼容核心或更新驱动。";
        if (lower.Contains("cannot load") || lower.Contains("could not load") || lower.Contains("library not found"))
            return "无法加载硬件编码运行库，请检查对应显卡驱动安装。";
        if (lower.Contains("no capable devices") || lower.Contains("no nvenc capable") || lower.Contains("no device") || lower.Contains("unsupported device"))
            return "未找到支持该编码器的设备，请检查显卡和驱动。";
        return "硬件试编码未通过，可查看技术详情；转换可使用 CPU。";
    }
}
