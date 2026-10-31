namespace TransFlux.Engine.FFmpeg.Core;

/// <summary>Resolved identity and absolute paths of the pinned media core.</summary>
public sealed record FfmpegCorePaths(string FfmpegPath, string FfprobePath, string CoreHash, string Version);
