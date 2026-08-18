using System.Security.Cryptography;

namespace Monkey.Core;

/// <summary>Ein zusammengehoerendes Vorschlagspaar fuer die beiden Telegram-Bots.</summary>
/// <param name="Pair">Das lesbare Wortpaar, etwa "Sunny Otter".</param>
public sealed record BotNames(
    string Pair,
    string MonkeyName,
    string MonkeyUsername,
    string FriendName,
    string FriendUsername);

/// <summary>
/// Erzeugt Vorschlaege fuer die zwei Bots, die der Telegram-Assistent bei
/// BotFather anlegen laesst. Anzeigenamen duerfen doppelt vorkommen,
/// Benutzernamen nicht - Eindeutigkeit ist also Pflicht, Lesbarkeit aber
/// genauso: Diese Namen werden abgetippt, vorgelesen und spaeter in einer
/// Chatliste wiedererkannt. Ein Wortpaar aus Eigenschaft und Tier plus einer
/// zweistelligen Zahl leistet beides, wo eine Folge von Hex-Zeichen nur das
/// erste kann. Beide Bots teilen sich das Wortpaar, damit sie in BotFather
/// sichtbar zusammengehoeren.
/// </summary>
public static class BotNameGenerator
{
    /// <summary>
    /// Kurze, gutmuetige Woerter. Die Laenge ist kein Geschmack, sondern
    /// Rechnung: Telegram erlaubt 32 Zeichen, und "monkey_" + Eigenschaft +
    /// "_" + Tier + zwei Ziffern + "_bot" muss darunter bleiben.
    /// </summary>
    public static readonly string[] Traits =
    [
        "brave", "breezy", "bright", "calm", "cheery", "chirpy", "clever", "cosy",
        "curly", "dandy", "eager", "fluffy", "fuzzy", "gentle", "happy", "jolly",
        "jumpy", "keen", "kind", "lively", "lucky", "mellow", "merry", "mild",
        "nimble", "noble", "plucky", "proud", "quiet", "ready", "silly", "sleepy",
        "smart", "snug", "sturdy", "sunny", "swift", "tidy", "warm", "wise",
        "witty", "zesty",
    ];

    public static readonly string[] Animals =
    [
        "badger", "beaver", "bison", "bunny", "camel", "crane", "dingo", "donkey",
        "eagle", "falcon", "ferret", "finch", "gecko", "goose", "heron", "hippo",
        "ibex", "koala", "lemur", "llama", "lynx", "magpie", "marten", "mole",
        "moose", "newt", "ocelot", "osprey", "otter", "panda", "parrot", "pigeon",
        "puffin", "quail", "rabbit", "raven", "robin", "seal", "shrew", "skunk",
        "sloth", "snail", "stoat", "swan", "tapir", "toucan", "turtle", "viper",
        "walrus", "weasel", "whale", "wombat", "zebra",
    ];

    /// <summary>Telegrams Obergrenze fuer Bot-Benutzernamen.</summary>
    public const int MaxUsernameLength = 32;

    public static BotNames Create()
    {
        var trait = Traits[RandomNumberGenerator.GetInt32(Traits.Length)];
        var animal = Animals[RandomNumberGenerator.GetInt32(Animals.Length)];

        // Die Zahl ist der eigentliche Eindeutigkeitsanteil: Zweistellig bleibt
        // sie vorlesbar, und zusammen mit dem Wortpaar sind es rund 200 000
        // Moeglichkeiten. Nimmt BotFather einen Namen trotzdem nicht, holt der
        // Nutzer sich mit einem Klick den naechsten Vorschlag.
        var number = RandomNumberGenerator.GetInt32(10, 100);

        var pair = $"{Capitalise(trait)} {Capitalise(animal)}";
        var slug = $"{trait}_{animal}{number}";

        return new BotNames(
            pair,
            $"Monkey Balance ({pair})",
            $"monkey_{slug}_bot",
            $"Monkey Friend ({pair})",
            $"friend_{slug}_bot");
    }

    private static string Capitalise(string word) =>
        char.ToUpperInvariant(word[0]) + word[1..];
}
