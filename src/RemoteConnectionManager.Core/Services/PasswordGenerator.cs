using System.Security.Cryptography;

namespace RemoteConnectionManager.Core.Services;

public enum PasswordKind
{
    Random,
    Memorable,
    Pin,
}

/// <summary>Options for <see cref="PasswordGenerator"/>. Length means characters, words or digits by kind.</summary>
public sealed record PasswordOptions
{
    public PasswordKind Kind { get; init; } = PasswordKind.Random;
    /// <summary>Random password length in characters.</summary>
    public int Characters { get; init; } = 32;
    public bool Numbers { get; init; } = true;
    public bool Symbols { get; init; }
    /// <summary>Memorable password length in words.</summary>
    public int Words { get; init; } = 4;
    /// <summary>Put between memorable words. Empty is allowed.</summary>
    public string Separator { get; init; } = "-";
    public bool Capitalise { get; init; } = true;
    /// <summary>Memorable: add one digit to the end of a random word.</summary>
    public bool AddNumber { get; init; } = true;
    /// <summary>PIN length in digits.</summary>
    public int Digits { get; init; } = 6;
}

/// <summary>
/// Makes passwords from the OS crypto random source, never <see cref="Random"/>. Nothing is
/// stored or logged; the caller decides where the value goes.
/// </summary>
public static class PasswordGenerator
{
    public const int MinCharacters = 8, MaxCharacters = 100;
    public const int MinWords = 3, MaxWords = 12;
    public const int MinDigits = 4, MaxDigits = 12;

    // Letters with I, l, O and 0's look alikes kept: length makes up for it and people paste, not type.
    public const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public const string Lower = "abcdefghijklmnopqrstuvwxyz";
    public const string Digit = "0123456789";
    /// <summary>Symbols that survive shells, connection strings and most web forms without quoting trouble.</summary>
    public const string Symbol = "!#$%&*+-.:=?@^_~";

    public static string Generate(PasswordOptions options) => options.Kind switch
    {
        PasswordKind.Memorable => Memorable(options),
        PasswordKind.Pin => Pin(options),
        _ => RandomPassword(options),
    };

    /// <summary>Approximate strength in bits, for the colour bar. Assumes the attacker knows the options.</summary>
    public static double EntropyBits(PasswordOptions options)
    {
        switch (options.Kind)
        {
            case PasswordKind.Memorable:
            {
                var words = Math.Clamp(options.Words, MinWords, MaxWords);
                var bits = words * Math.Log2(PasswordWords.All.Count);
                if (options.AddNumber) bits += Math.Log2(10 * words);
                return bits;
            }
            case PasswordKind.Pin:
                return Math.Clamp(options.Digits, MinDigits, MaxDigits) * Math.Log2(10);
            default:
                return Math.Clamp(options.Characters, MinCharacters, MaxCharacters) * Math.Log2(Pool(options).Length);
        }
    }

    private static string Pool(PasswordOptions o) =>
        Upper + Lower + (o.Numbers ? Digit : string.Empty) + (o.Symbols ? Symbol : string.Empty);

    private static string RandomPassword(PasswordOptions o)
    {
        var length = Math.Clamp(o.Characters, MinCharacters, MaxCharacters);
        var pool = Pool(o);
        // Sites often insist on one of each class, so redraw until every chosen class is present.
        // With 8+ characters this almost always passes first time.
        while (true)
        {
            var chars = new char[length];
            for (var i = 0; i < length; i++) chars[i] = pool[RandomNumberGenerator.GetInt32(pool.Length)];
            if (chars.Any(char.IsAsciiLetterUpper) && chars.Any(char.IsAsciiLetterLower)
                && (!o.Numbers || chars.Any(char.IsAsciiDigit))
                && (!o.Symbols || chars.Any(c => Symbol.Contains(c))))
                return new string(chars);
        }
    }

    private static string Memorable(PasswordOptions o)
    {
        var count = Math.Clamp(o.Words, MinWords, MaxWords);
        var words = new string[count];
        for (var i = 0; i < count; i++)
        {
            var word = PasswordWords.All[RandomNumberGenerator.GetInt32(PasswordWords.All.Count)];
            words[i] = o.Capitalise ? char.ToUpperInvariant(word[0]) + word[1..] : word;
        }
        if (o.AddNumber)
        {
            var at = RandomNumberGenerator.GetInt32(count);
            words[at] += RandomNumberGenerator.GetInt32(10).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return string.Join(o.Separator ?? string.Empty, words);
    }

    private static string Pin(PasswordOptions o)
    {
        var length = Math.Clamp(o.Digits, MinDigits, MaxDigits);
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        return new string(chars);
    }
}
