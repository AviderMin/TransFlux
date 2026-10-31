using TransFlux.Domain.Rules;

namespace TransFlux.Domain.Tests;

public class OutputNamingTests
{
    [Fact]
    public void DefaultFolderName_IsStable()
    {
        Assert.Equal("TransFlux Output", OutputNaming.DefaultOutputFolderName);
    }

    [Fact]
    public void SanitizeStem_ReplacesInvalidCharacters()
    {
        Assert.Equal("a_b_c_d_e_f_g_h_i_j", OutputNaming.SanitizeStem("a<b>c:d\"e/f\\g|h?i*j"));
    }

    [Fact]
    public void SanitizeStem_StripsTrailingDotsAndSpaces()
    {
        Assert.Equal("name", OutputNaming.SanitizeStem("name.  "));
        Assert.Equal("name", OutputNaming.SanitizeStem("name..."));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void SanitizeStem_EmptyResult_FallsBackToOutput(string input)
    {
        Assert.Equal("output", OutputNaming.SanitizeStem(input));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con", "_con")]
    [InlineData("COM1", "_COM1")]
    [InlineData("LPT9", "_LPT9")]
    public void SanitizeStem_EscapesWindowsReservedNames(string input, string expected)
    {
        Assert.Equal(expected, OutputNaming.SanitizeStem(input));
    }

    [Fact]
    public void SanitizeStem_ReplacesControlCharacters()
    {
        Assert.Equal("a_b", OutputNaming.SanitizeStem("a\tb"));
    }

    [Fact]
    public void SanitizeStem_ClampsLength()
    {
        var result = OutputNaming.SanitizeStem(new string('a', 250));
        Assert.Equal(180, result.Length);
    }

    [Fact]
    public void AllocateUniqueStem_ReturnsFirstFreeName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-output");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(dir, "movie.mp4") };
        var result = OutputNaming.AllocateUniqueStem(dir, "movie", ".mp4", p => taken.Contains(p));
        Assert.Equal(Path.Combine(dir, "movie (2).mp4"), result);
    }

    [Fact]
    public void AllocateUniqueStem_SanitizesStemBeforeAllocating()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-output");
        var result = OutputNaming.AllocateUniqueStem(dir, "a:b", ".mp4", _ => false);
        Assert.Equal(Path.Combine(dir, "a_b.mp4"), result);
    }

    [Fact]
    public void AllocateUniqueStem_SkipsMultipleExistingNames()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-output");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(dir, "clip.mp4"),
            Path.Combine(dir, "clip (2).mp4"),
        };
        var result = OutputNaming.AllocateUniqueStem(dir, "clip", ".mp4", p => taken.Contains(p));
        Assert.Equal(Path.Combine(dir, "clip (3).mp4"), result);
    }

    [Fact]
    public void DefaultOutputDirectory_PlacesFolderBesideSource()
    {
        var source = @"C:\videos\clip.mp4";
        var expected = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, "TransFlux Output");
        Assert.Equal(expected, OutputNaming.DefaultOutputDirectory(source));
    }
}
