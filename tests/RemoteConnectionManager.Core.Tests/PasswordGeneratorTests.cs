using RemoteConnectionManager.Core.Services;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class PasswordGeneratorTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    public void RandomHasRequestedLength(int length)
    {
        var value = PasswordGenerator.Generate(new PasswordOptions { Characters = length });
        Assert.Equal(length, value.Length);
    }

    [Fact]
    public void RandomLengthIsClamped()
    {
        Assert.Equal(PasswordGenerator.MinCharacters, PasswordGenerator.Generate(new PasswordOptions { Characters = 1 }).Length);
        Assert.Equal(PasswordGenerator.MaxCharacters, PasswordGenerator.Generate(new PasswordOptions { Characters = 5000 }).Length);
    }

    [Fact]
    public void RandomHasEveryChosenClass()
    {
        for (var i = 0; i < 200; i++)
        {
            var value = PasswordGenerator.Generate(new PasswordOptions { Characters = 8, Numbers = true, Symbols = true });
            Assert.Contains(value, char.IsAsciiLetterUpper);
            Assert.Contains(value, char.IsAsciiLetterLower);
            Assert.Contains(value, char.IsAsciiDigit);
            Assert.Contains(value, c => PasswordGenerator.Symbol.Contains(c));
        }
    }

    [Fact]
    public void RandomLeavesOutUnchosenClasses()
    {
        for (var i = 0; i < 50; i++)
        {
            var value = PasswordGenerator.Generate(new PasswordOptions { Characters = 64, Numbers = false, Symbols = false });
            Assert.All(value, c => Assert.True(char.IsAsciiLetter(c)));
        }
    }

    [Fact]
    public void RandomValuesDiffer()
    {
        var a = PasswordGenerator.Generate(new PasswordOptions());
        var b = PasswordGenerator.Generate(new PasswordOptions());
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MemorableUsesWordsAndSeparator()
    {
        var value = PasswordGenerator.Generate(new PasswordOptions
            { Kind = PasswordKind.Memorable, Words = 5, Separator = ".", Capitalise = false, AddNumber = false });
        var parts = value.Split('.');
        Assert.Equal(5, parts.Length);
        Assert.All(parts, p => Assert.Contains(p, PasswordWords.All));
    }

    [Fact]
    public void MemorableCapitalisesAndAddsOneDigit()
    {
        var value = PasswordGenerator.Generate(new PasswordOptions
            { Kind = PasswordKind.Memorable, Words = 4, Separator = "-", Capitalise = true, AddNumber = true });
        var parts = value.Split('-');
        Assert.Equal(4, parts.Length);
        Assert.All(parts, p => Assert.True(char.IsAsciiLetterUpper(p[0])));
        Assert.Equal(1, value.Count(char.IsAsciiDigit));
    }

    [Fact]
    public void PinIsDigitsOnly()
    {
        var value = PasswordGenerator.Generate(new PasswordOptions { Kind = PasswordKind.Pin, Digits = 8 });
        Assert.Equal(8, value.Length);
        Assert.All(value, c => Assert.True(char.IsAsciiDigit(c)));
    }

    [Fact]
    public void WordListIsCleanAndLargeEnough()
    {
        Assert.True(PasswordWords.All.Count >= 600, $"only {PasswordWords.All.Count} words");
        Assert.All(PasswordWords.All, w => Assert.Matches("^[a-z]{3,8}$", w));
    }

    [Fact]
    public void EntropyGrowsWithLengthAndPool()
    {
        var shortPlain = PasswordGenerator.EntropyBits(new PasswordOptions { Characters = 12, Numbers = false });
        var shortMixed = PasswordGenerator.EntropyBits(new PasswordOptions { Characters = 12, Numbers = true, Symbols = true });
        var longMixed = PasswordGenerator.EntropyBits(new PasswordOptions { Characters = 32, Numbers = true, Symbols = true });
        Assert.True(shortPlain < shortMixed);
        Assert.True(shortMixed < longMixed);
        Assert.InRange(PasswordGenerator.EntropyBits(new PasswordOptions { Kind = PasswordKind.Pin, Digits = 6 }), 19, 20);
    }
}
