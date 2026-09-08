using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RuneFoundry.Core.Formats;

namespace RuneFoundry.UI;

/// <summary>Something the wave can be made of, and how many to keep.</summary>
public sealed class WaveUnit : INotifyPropertyChanged
{
    public required byte Variable { get; init; }
    public required string Name { get; init; }

    private int _count;

    /// <summary>
    /// How many to keep. Typed rather than picked: the game takes any number up to 255,
    /// and a list of all of them is a poor way to say four.
    /// </summary>
    public int Count
    {
        get => _count;
        set
        {
            var wanted = Math.Clamp(value, 0, byte.MaxValue);
            if (_count == wanted) return;

            _count = wanted;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One way the wave attacks. A wave can use more than one at a time.
/// </summary>
public sealed class WaveDomain : INotifyPropertyChanged
{
    public required AiTemplates.AttackDomain Domain { get; init; }
    public required string Label { get; init; }
    public required IReadOnlyList<string> SizeChoices { get; init; }
    public required IReadOnlyList<WaveUnit> Units { get; init; }

    private bool _isOn;
    private int _sizeIndex = 2;

    public bool IsOn
    {
        get => _isOn;
        set { if (Set(ref _isOn, value)) Raise(nameof(IsOn)); }
    }

    public int SizeIndex
    {
        get => _sizeIndex;
        set { if (Set(ref _sizeIndex, value)) Raise(nameof(SizeIndex)); }
    }

    private bool Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        return true;
    }

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Describing an attack wave in words, and getting the instructions written for you.
///
/// A wave is a sleep, then for each way it attacks: the units to keep making, the size of
/// the party to gather, a wait for it, and the launch. Which variables those are depends on
/// the domain: the party size is 0x0D, 0x0F or 0x11, the launch 0x09, 0x0A or 0x0B, and the
/// wait 0x04, 0x05 or 0x06. Nobody should have to hold that.
///
/// The shape is the game's own: 322 of its 480 waves wait immediately before launching, and
/// 50 stretches launch more than one attack at once.
/// </summary>
public partial class WaveWizard : Window
{
    private static readonly int[] Sizes = { 1, 2, 3, 4, 5, 6, 8, 10 };

    /// <summary>Delays in seconds, from a quick rush to a long build-up.</summary>
    private static readonly int[] Delays = { 30, 60, 120, 180, 300, 420, 600 };

    private readonly int _roomBytes;
    private readonly bool _buildsAlready;
    private List<WaveDomain> _domains = new();

    public WaveWizard(int roomBytes, bool buildsAlready)
    {
        InitializeComponent();

        _roomBytes = roomBytes;
        _buildsAlready = buildsAlready;

        DelayBox.ItemsSource = Delays.Select(Describe).ToList();
        DelayBox.SelectedIndex = 2;

        ShowDomains();
        Describe();
    }

    /// <summary>The wave the wizard describes, once it has been accepted.</summary>
    public List<AiInstruction>? Wave { get; private set; }

    private static string Describe(int seconds) =>
        seconds < 60 ? $"{seconds} seconds"
        : seconds == 60 ? "a minute"
        : $"{seconds / 60} minutes";

    private void ShowDomains()
    {
        var sizes = Sizes.Select(n => n == 1 ? "gathers 1" : $"gathers {n}").ToList();

        _domains = new List<WaveDomain>
        {
            Make(AiTemplates.AttackDomain.Land, "By land", sizes),
            Make(AiTemplates.AttackDomain.Naval, "By sea", sizes),
            Make(AiTemplates.AttackDomain.Air, "By air", sizes),
        };

        // Land on its own is the common wave, so it opens ready to accept.
        _domains[0].IsOn = true;

        // A script that already asks for units keeps getting them, so a later wave that
        // repeats the request only spends bytes saying what is already true.
        var first = _domains[0].Units.FirstOrDefault();
        if (first is not null && !_buildsAlready) first.Count = 4;

        foreach (var domain in _domains)
        {
            domain.PropertyChanged += (_, _) => Describe();
            foreach (var unit in domain.Units) unit.PropertyChanged += (_, _) => Describe();
        }

        DomainList.ItemsSource = _domains;
    }

    private static WaveDomain Make(
        AiTemplates.AttackDomain domain, string label, IReadOnlyList<string> sizes)
        => new()
        {
            Domain = domain,
            Label = label,
            SizeChoices = sizes,
            Units = AiTemplates.UnitsFor(domain)
                .Select(v => new WaveUnit { Variable = v, Name = AiNames.Variable(v) })
                .ToList(),
        };

    private List<AiTemplates.AttackPart> Parts() => _domains
        .Where(d => d.IsOn)
        .Select(d => new AiTemplates.AttackPart(
            d.Domain,
            (byte)Sizes[Math.Clamp(d.SizeIndex, 0, Sizes.Length - 1)],
            d.Units.Where(u => u.Count > 0)
                   .ToDictionary(u => u.Variable, u => (byte)u.Count)))
        .ToList();

    private List<AiInstruction> Build() =>
        AiTemplates.AttackWave(Parts(), Delays[Math.Max(0, DelayBox.SelectedIndex)]);

    private void OnChanged(object sender, SelectionChangedEventArgs e) => Describe();

    private void Describe()
    {
        if (DelayBox?.ItemsSource is null || Preview is null) return;

        var parts = Parts();
        var wave = Build();
        var bytes = wave.Sum(i => i.Length);

        var said = new List<string>();
        foreach (var domain in _domains.Where(d => d.IsOn))
        {
            var size = Sizes[Math.Clamp(domain.SizeIndex, 0, Sizes.Length - 1)];
            var makes = domain.Units.Where(u => u.Count > 0).ToList();
            var word = domain.Label.ToLowerInvariant();

            said.Add(makes.Count == 0
                ? $"gathers {size} and attacks {word}"
                : "keeps " + string.Join(", ", makes.Select(u => $"{u.Count} {u.Name}"))
                  + $", gathers {size} and attacks {word}");
        }

        Preview.Text = said.Count == 0
            ? "This wave does nothing. Choose at least one way to attack."
            : $"Waits {Describe(Delays[Math.Max(0, DelayBox.SelectedIndex)])}, then "
              + string.Join("; then ", said)
              + $". {wave.Count} instructions, {bytes} bytes.";

        var fits = bytes <= _roomBytes;

        // The wait for a party does not give up. A wave that asks for more than it makes
        // stops the script for the rest of the mission, so this is said here, not buried.
        var starved = _domains.FirstOrDefault(d =>
            d.IsOn
            && d.Units.Any(u => u.Count > 0)
            && d.Units.Sum(u => u.Count) < Sizes[Math.Clamp(d.SizeIndex, 0, Sizes.Length - 1)]);

        Room.Text = parts.Count == 0
            ? "Nothing to add yet."
            : !fits
                ? $"Too big. The script has {_roomBytes} bytes free. This needs {bytes}."
                : starved is not null
                    ? $"{starved.Label} gathers more than it builds. That wait never ends. "
                      + "Build more, or gather fewer."
                    : $"{_roomBytes} bytes free. This fits.";

        AddButton.IsEnabled = fits && parts.Count > 0;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        Wave = Build();
        DialogResult = true;
    }

    /// <summary>Asks for a wave. Null when the dialog was cancelled.</summary>
    public static List<AiInstruction>? Ask(Window? owner, int roomBytes, bool buildsAlready = false)
    {
        var wizard = new WaveWizard(roomBytes, buildsAlready) { Owner = owner };
        return wizard.ShowDialog() == true ? wizard.Wave : null;
    }
}
