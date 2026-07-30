using Docket.Runner;

namespace Docket.Runner.Tests;

/// <summary>
/// The enrollment token must be read from a file or stdin, NEVER from argv
/// (spec §13: an argv token lands in shell history and the process list, and the
/// machine token it yields is the highest-value secret on the machine). These pin
/// the token-source behavior of <see cref="Program.ReadEnrollmentTokenAsync"/>.
/// </summary>
public sealed class EnrollmentTokenSourceTests
{
    [Fact]
    public async Task Reads_and_trims_the_token_from_a_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"docket-enroll-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "  dkt_e_fromfile\n");
        try
        {
            Assert.Equal("dkt_e_fromfile", await Program.ReadEnrollmentTokenAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Reads_and_trims_the_token_from_stdin_when_no_file_given()
    {
        var original = Console.In;
        Console.SetIn(new StringReader("dkt_e_fromstdin\n"));
        try
        {
            Assert.Equal("dkt_e_fromstdin", await Program.ReadEnrollmentTokenAsync(null));
        }
        finally { Console.SetIn(original); }
    }

    [Fact]
    public async Task Returns_null_when_the_named_token_file_is_unreadable()
    {
        // A missing file is a clean refusal (the caller exits 2), not a throw.
        var missing = Path.Combine(Path.GetTempPath(), $"docket-enroll-missing-{Guid.NewGuid():N}");
        Assert.Null(await Program.ReadEnrollmentTokenAsync(missing));
    }
}
