using System.IO;
using System.Windows;
using System.Windows.Controls;
using RuneFoundry.Core;
using RuneFoundry.UI;

namespace RuneFoundry.Editor;

/// <summary>One of the game's sound files, as the picker lists it.</summary>
public sealed class SoundChoice : Observable
{
    public required string Path { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The folder it sits in, which is how the game groups speakers.</summary>
    public string Folder => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";

    private bool _isPlaying;

    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    public override string ToString() => FileName;
}

/// <summary>
/// Picks one of the game's own sounds.
///
/// Swapping a sound is the common case — a Peasant that says what the Peon says — and it
/// needs no file of your own, so this lists what the install already has and copies the
/// chosen one into the mod under the name the unit plays.
/// </summary>
public partial class SoundPickerWindow : Window
{
    private readonly GameInstall _game;
    private readonly AudioPlayer _audio = new();
    private readonly List<SoundChoice> _all = new();

    public SoundPickerWindow(GameInstall game, SoundLibrary library, string forFileName)
    {
        InitializeComponent();
        RuneFoundry.UI.Palette.FollowTitleBar(this);

        _game = game;
        _audio.Changed += RefreshPlaying;

        Heading.Text = $"Choose a sound to play as {forFileName}";
        Note.Text = $"{library.Count} sounds ship with the game. The one you pick is copied "
                    + "into the mod under that name.";

        foreach (var path in library.All) _all.Add(new SoundChoice { Path = path });
        _all.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        Show(_all);
        Closed += (_, _) => _audio.Stop();
    }

    /// <summary>The game path of the chosen sound, or null when the window was cancelled.</summary>
    public string? Chosen { get; private set; }

    private void Show(IEnumerable<SoundChoice> choices) => SoundList.ItemsSource = choices.ToList();

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var filter = FilterBox.Text.Trim();
        Show(filter.Length == 0
            ? _all
            : _all.Where(c => c.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UseButton.IsEnabled = SoundList.SelectedItem is SoundChoice;

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SoundChoice choice) return;

        var error = _audio.Toggle(_game.ResolveDataPath(choice.Path));
        if (error is not null) Note.Text = error;

        RefreshPlaying();
    }

    private void RefreshPlaying()
    {
        foreach (var choice in _all)
            choice.IsPlaying = _audio.IsPlaying_(_game.ResolveDataPath(choice.Path));
    }

    private void OnUseChosen(object sender, RoutedEventArgs e)
    {
        if (SoundList.SelectedItem is not SoundChoice choice) return;

        Chosen = choice.Path;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
