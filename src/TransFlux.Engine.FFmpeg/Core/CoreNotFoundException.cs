namespace TransFlux.Engine.FFmpeg.Core;

/// <summary>Thrown when no pinned ffmpeg/ffprobe pair can be located. Never falls back to PATH.</summary>
public sealed class CoreNotFoundException : Exception
{
    public CoreNotFoundException(string message) : base(message) { }
}
