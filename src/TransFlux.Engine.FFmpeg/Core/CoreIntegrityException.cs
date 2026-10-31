namespace TransFlux.Engine.FFmpeg.Core;

/// <summary>
/// Thrown when a resolved core binary does not match the SHA-256 recorded in
/// core-manifest.json. Carries both hashes so the failure can be diagnosed and
/// reported without silently falling back to PATH.
/// </summary>
public sealed class CoreIntegrityException : Exception
{
    public string FilePath { get; }
    public string ExpectedHash { get; }
    public string ActualHash { get; }

    public CoreIntegrityException(string filePath, string expectedHash, string actualHash)
        : base($"Core integrity check failed for '{filePath}': expected SHA-256 {expectedHash} but found {actualHash}.")
    {
        FilePath = filePath;
        ExpectedHash = expectedHash;
        ActualHash = actualHash;
    }
}
