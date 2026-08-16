using System.Security.Cryptography;

namespace Monkey.Core;

/// <summary>
/// Erzeugt Masterpasswort-Vorschlaege. Das Alphabet laesst verwechselbare
/// Zeichen weg (0/O, 1/l/I), damit sich ein notiertes oder am Telefon
/// vorgelesenes Passwort fehlerfrei uebertragen laesst; die Bindestrich-
/// Gruppen machen es abtippbar. Vier Gruppen zu vier Zeichen aus 57 Symbolen
/// sind gut 90 Bit - weit jenseits dessen, was PBKDF2 hier braucht.
/// </summary>
public static class PasswordGenerator
{
    public const string Alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Create(int groups = 4, int groupLength = 4)
    {
        var chars = new char[groups * groupLength + groups - 1];
        var at = 0;

        for (var group = 0; group < groups; group++)
        {
            if (group > 0) chars[at++] = '-';
            for (var i = 0; i < groupLength; i++)
                chars[at++] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }
}
