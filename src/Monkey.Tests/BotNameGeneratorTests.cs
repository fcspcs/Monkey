using Monkey.Core;
using Xunit;

namespace Monkey.Tests;

public sealed class BotNameGeneratorTests
{
    [Fact]
    public void Create_ProducesUsernamesTelegramAccepts()
    {
        // Telegram: 5 bis 32 Zeichen, Beginn mit einem Buchstaben, nur
        // Buchstaben, Ziffern und Unterstriche, Ende auf "bot".
        for (var i = 0; i < 200; i++)
        {
            var names = BotNameGenerator.Create();

            foreach (var username in new[] { names.MonkeyUsername, names.FriendUsername })
            {
                Assert.InRange(username.Length, 5, BotNameGenerator.MaxUsernameLength);
                Assert.True(char.IsAsciiLetterLower(username[0]));
                Assert.EndsWith("_bot", username, StringComparison.Ordinal);
                Assert.All(username, c =>
                    Assert.True(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'));
            }

            Assert.NotEqual(names.MonkeyUsername, names.FriendUsername);
            Assert.NotEqual(names.MonkeyName, names.FriendName);
        }
    }

    [Fact]
    public void Create_KeepsBothBotsOnTheSameWordPair()
    {
        var names = BotNameGenerator.Create();

        Assert.Contains(names.Pair, names.MonkeyName, StringComparison.Ordinal);
        Assert.Contains(names.Pair, names.FriendName, StringComparison.Ordinal);

        var slug = names.MonkeyUsername["monkey_".Length..];
        Assert.Equal($"friend_{slug}", names.FriendUsername);
    }

    [Fact]
    public void Create_ReadsAsWordsRatherThanRandomCharacters()
    {
        var names = BotNameGenerator.Create();
        var words = names.Pair.Split(' ');

        Assert.Equal(2, words.Length);
        Assert.Contains(words[0].ToLowerInvariant(), BotNameGenerator.Traits);
        Assert.Contains(words[1].ToLowerInvariant(), BotNameGenerator.Animals);
    }

    [Fact]
    public void WordLists_StayShortEnoughForTheUsernameLimit()
    {
        // Der laengste denkbare Benutzername muss Telegrams Grenze halten -
        // sonst faellt genau die eine ungluecklichste Kombination durch.
        var longestTrait = BotNameGenerator.Traits.Max(word => word.Length);
        var longestAnimal = BotNameGenerator.Animals.Max(word => word.Length);
        var longest = "monkey_".Length + longestTrait + 1 + longestAnimal + 2 + "_bot".Length;

        Assert.True(longest <= BotNameGenerator.MaxUsernameLength,
            $"The longest possible username would be {longest} characters.");

        Assert.All(BotNameGenerator.Traits.Concat(BotNameGenerator.Animals), word =>
            Assert.All(word, c => Assert.True(char.IsAsciiLetterLower(c))));
    }

    [Fact]
    public void Create_DoesNotRepeatItself()
    {
        // Rund 200 000 Kombinationen: Zwei gleiche Vorschlaege hintereinander
        // waeren ein kaputter Generator, kein Zufall.
        Assert.NotEqual(BotNameGenerator.Create().MonkeyUsername,
                        BotNameGenerator.Create().MonkeyUsername);
    }
}
