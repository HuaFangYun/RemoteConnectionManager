using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnectionManager.Core.Services;

namespace RemoteConnectionManager.App.ViewModels;

public sealed record PasswordKindOption(PasswordKind Kind, string Label)
{
    public override string ToString() => Label;
}

public sealed record SeparatorOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The password generator popup. Makes a new value on every option change. The value lives only
/// in this view model; the options (never the value) go back to settings when the popup closes.
/// </summary>
public sealed partial class PasswordGeneratorViewModel : ObservableObject
{
    private int _characters;
    private int _words;
    private int _digits;

    [ObservableProperty] private PasswordKindOption _selectedKind;
    [ObservableProperty] private bool _numbers;
    [ObservableProperty] private bool _symbols;
    [ObservableProperty] private SeparatorOption _separator;
    [ObservableProperty] private bool _capitalise;
    [ObservableProperty] private bool _addNumber;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private double _strengthFraction;
    [ObservableProperty] private string _strengthLevel = "Ok";
    [ObservableProperty] private string _strengthText = string.Empty;

    public IReadOnlyList<PasswordKindOption> Kinds { get; } =
    [
        new(PasswordKind.Random, "Random Password"),
        new(PasswordKind.Memorable, "Memorable Password"),
        new(PasswordKind.Pin, "PIN Code"),
    ];

    public IReadOnlyList<SeparatorOption> Separators { get; } =
    [
        new("-", "Hyphen (-)"),
        new(".", "Period (.)"),
        new("_", "Underscore (_)"),
        new(",", "Comma (,)"),
        new(" ", "Space"),
        new("", "None"),
    ];

    /// <summary>Text on the accent button: "Use" fills a field, "Copy" puts it on the clipboard.</summary>
    public string ActionLabel { get; }

    public PasswordGeneratorViewModel(PasswordOptions start, string actionLabel)
    {
        ActionLabel = actionLabel;
        _characters = Math.Clamp(start.Characters, PasswordGenerator.MinCharacters, PasswordGenerator.MaxCharacters);
        _words = Math.Clamp(start.Words, PasswordGenerator.MinWords, PasswordGenerator.MaxWords);
        _digits = Math.Clamp(start.Digits, PasswordGenerator.MinDigits, PasswordGenerator.MaxDigits);
        _selectedKind = Kinds.FirstOrDefault(k => k.Kind == start.Kind) ?? Kinds[0];
        _numbers = start.Numbers;
        _symbols = start.Symbols;
        _separator = Separators.FirstOrDefault(s => s.Value == start.Separator) ?? Separators[0];
        _capitalise = start.Capitalise;
        _addNumber = start.AddNumber;
        Regenerate();
    }

    public bool IsRandom => SelectedKind.Kind == PasswordKind.Random;
    public bool IsMemorable => SelectedKind.Kind == PasswordKind.Memorable;

    public string LengthLabel => SelectedKind.Kind switch
    {
        PasswordKind.Memorable => "Words",
        PasswordKind.Pin => "Digits",
        _ => "Characters",
    };

    public int LengthMin => SelectedKind.Kind switch
    {
        PasswordKind.Memorable => PasswordGenerator.MinWords,
        PasswordKind.Pin => PasswordGenerator.MinDigits,
        _ => PasswordGenerator.MinCharacters,
    };

    public int LengthMax => SelectedKind.Kind switch
    {
        PasswordKind.Memorable => PasswordGenerator.MaxWords,
        PasswordKind.Pin => PasswordGenerator.MaxDigits,
        _ => PasswordGenerator.MaxCharacters,
    };

    /// <summary>Characters, words or digits, by kind. Clamped to the generator's limits.</summary>
    public int Length
    {
        get => SelectedKind.Kind switch
        {
            PasswordKind.Memorable => _words,
            PasswordKind.Pin => _digits,
            _ => _characters,
        };
        set
        {
            switch (SelectedKind.Kind)
            {
                case PasswordKind.Memorable: _words = Math.Clamp(value, PasswordGenerator.MinWords, PasswordGenerator.MaxWords); break;
                case PasswordKind.Pin: _digits = Math.Clamp(value, PasswordGenerator.MinDigits, PasswordGenerator.MaxDigits); break;
                default: _characters = Math.Clamp(value, PasswordGenerator.MinCharacters, PasswordGenerator.MaxCharacters); break;
            }
            OnPropertyChanged();
            Regenerate();
        }
    }

    public PasswordOptions Options => new()
    {
        Kind = SelectedKind.Kind,
        Characters = _characters,
        Numbers = Numbers,
        Symbols = Symbols,
        Words = _words,
        Separator = Separator.Value,
        Capitalise = Capitalise,
        AddNumber = AddNumber,
        Digits = _digits,
    };

    partial void OnSelectedKindChanged(PasswordKindOption value)
    {
        OnPropertyChanged(nameof(IsRandom));
        OnPropertyChanged(nameof(IsMemorable));
        OnPropertyChanged(nameof(LengthLabel));
        OnPropertyChanged(nameof(LengthMin));
        OnPropertyChanged(nameof(LengthMax));
        OnPropertyChanged(nameof(Length));
        Regenerate();
    }

    partial void OnNumbersChanged(bool value) => Regenerate();
    partial void OnSymbolsChanged(bool value) => Regenerate();
    partial void OnSeparatorChanged(SeparatorOption value) => Regenerate();
    partial void OnCapitaliseChanged(bool value) => Regenerate();
    partial void OnAddNumberChanged(bool value) => Regenerate();

    [RelayCommand]
    private void Regenerate()
    {
        var options = Options;
        Password = PasswordGenerator.Generate(options);
        var bits = PasswordGenerator.EntropyBits(options);
        StrengthFraction = Math.Clamp(bits / 128, 0.04, 1);
        (StrengthLevel, var word) = bits switch
        {
            < 40 => ("Weak", "Weak"),
            < 64 => ("Fair", "Fair"),
            < 100 => ("Ok", "Strong"),
            _ => ("Ok", "Very strong"),
        };
        StrengthText = $"{word}, about {bits:0} bits";
    }
}
