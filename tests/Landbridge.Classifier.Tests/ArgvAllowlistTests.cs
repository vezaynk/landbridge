using Landbridge.Classifier;

namespace Landbridge.Classifier.Tests;

public sealed class ArgvAllowlistTests
{
    [Theory]
    [InlineData("ls")]
    [InlineData("ls -la")]
    [InlineData("cat README.md")]
    [InlineData("which git")]
    [InlineData("git status")]
    [InlineData("git --version")]
    [InlineData("git version")]
    [InlineData("git log --oneline")]
    [InlineData("/usr/bin/git status")]
    [InlineData("pwd")]
    public void Simple_allowlisted_argv_is_readonly(string command) =>
        Assert.True(ArgvAllowlist.IsSimpleAllowlisted(command));

    [Theory]
    [InlineData("git --version || true")]
    [InlineData("which git || true")]
    [InlineData("cat file > out")]
    [InlineData("echo hi | cat")]
    [InlineData("echo $HOME")]
    [InlineData("git fetch")]
    [InlineData("git push")]
    [InlineData("git -v push")]
    [InlineData("git -C repo status")]
    [InlineData("npm test")]
    [InlineData("rm -rf /")]
    [InlineData("bash -c 'git status'")]
    [InlineData("printf hi")]
    [InlineData("mkdir -p probe-dir")]
    public void Metacharacters_or_unknown_programs_are_not_readonly(string command) =>
        Assert.False(ArgvAllowlist.IsSimpleAllowlisted(command));
}
