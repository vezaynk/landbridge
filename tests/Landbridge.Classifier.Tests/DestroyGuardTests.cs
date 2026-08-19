using Landbridge.Classifier;

namespace Landbridge.Classifier.Tests;

public sealed class DestroyGuardTests
{
    [Theory]
    [InlineData("git reset --hard HEAD")]
    [InlineData("git checkout -- .")]
    [InlineData("git clean -fd")]
    [InlineData("git stash drop")]
    [InlineData("git commit --amend --no-edit")]
    [InlineData("terraform destroy -auto-approve")]
    [InlineData("bash -lc \"git reset --hard\"")]
    public void Matches_destroy_list(string command) =>
        Assert.True(DestroyGuard.Match(command).Blocked);

    [Theory]
    [InlineData("git status")]
    [InlineData("npm test")]
    public void Non_destroy_is_clean(string command) =>
        Assert.False(DestroyGuard.Match(command).Blocked);
}
