using Monkey.Core;
using Xunit;

namespace Monkey.Tests;

public sealed class PasswordGeneratorTests
{
    [Fact]
    public void Create_MeetsTheMasterPasswordMinimum()
    {
        Assert.True(PasswordGenerator.Create().Length >= PasswordHash.MinimumLength);
    }

    [Fact]
    public void Create_UsesGroupsFromTheSafeAlphabet()
    {
        var generated = PasswordGenerator.Create(groups: 4, groupLength: 4);
        var groups = generated.Split('-');

        Assert.Equal(4, groups.Length);
        Assert.All(groups, group =>
        {
            Assert.Equal(4, group.Length);
            Assert.All(group, c => Assert.Contains(c, PasswordGenerator.Alphabet));
        });
    }

    [Fact]
    public void Create_DoesNotRepeatItself()
    {
        // Bei ueber 90 Bit Zufall waere eine Wiederholung ein kaputter Generator.
        Assert.NotEqual(PasswordGenerator.Create(), PasswordGenerator.Create());
    }
}
