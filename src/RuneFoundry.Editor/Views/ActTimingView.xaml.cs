using System.IO;
using System.Windows;
using System.Windows.Controls;
using RuneFoundry.Core;
using RuneFoundry.Core.Formats;
using RuneFoundry.UI;

namespace RuneFoundry.Editor.Views;

/// <summary>
/// How long an act's title card stays on screen.
///
/// The game has no timer for it. The act screen at <c>0x524760</c> draws the card, starts the
/// fanfare, and then loops while any audio channel is still busy — so the card is up for
/// exactly as long as the fanfare runs. That is a genuinely useful thing to be able to set,
/// and nobody would guess it was hiding in <c>Sfx/Hact.wav</c>, so it is offered here as the
/// duration it really is rather than as a sound file to go and edit.
///
/// Setting it pads the fanfare with silence. Reading it back is the reverse, so the control
/// always shows what the game will actually do, with no stored state of its own to disagree.
/// </summary>
public partial class ActTimingView : UserControl
{
    private Session _session = null!;
    private ModProject? _project;
    private MediaFile? _fanfare;

    /// <summary>The shortest the card can stay: the fanfare with its trailing silence gone.</summary>
    private TimeSpan _floor;

    /// <summary>Set while the control is being filled in, so the slider does not report back.</summary>
    private bool _loading;

    public ActTimingView() => InitializeComponent();

    public void Attach(Session session) => _session = session;

    /// <summary>Reports progress to whatever is hosting this.</summary>
    public event Action<string>? Status;

    /// <summary>Raised when the fanfare was added to or dropped from the mod.</summary>
    public event Action? OverridesChanged;

    private Window? Owner => Window.GetWindow(this);

    public void Refresh(ModProject? project, string campaignId)
    {
        _project = project;
        _fanfare = GameMedia.Fanfare(campaignId).Files[0];

        var race = campaignId.EndsWith("orc", StringComparison.Ordinal) ? "orc" : "human";
        Blurb.Text =
            "Each act opens on its title card. The game holds that card until the act fanfare "
            + $"stops. The other {race} campaigns share this setting.";

        Load();
    }

    /// <summary>
    /// Reads the current and stock fanfare and puts the slider where they say it should be.
    ///
    /// Read on this thread deliberately: the two files are about 110 KB each, which is far
    /// too small to be worth the flicker of a background pass.
    /// </summary>
    private void Load()
    {
        _loading = true;

        try
        {
            if (_fanfare is null || _session?.Game is null) { IsEnabled = false; return; }

            var stockPath = _session.StockFile(_fanfare.Path);
            var minePath = _project?.HasOverride(_fanfare.Path) == true
                ? _project.ResolveContentPath(_fanfare.Path)
                : stockPath;

            if (!File.Exists(minePath) || !File.Exists(stockPath))
            {
                IsEnabled = false;
                State.Text = "The game's fanfare is missing, so this cannot be set.";
                return;
            }

            IsEnabled = true;

            var mine = File.ReadAllBytes(minePath);
            var now = WaveFile.Describe(mine)?.Duration ?? TimeSpan.Zero;
            var stock = WaveFile.Describe(File.ReadAllBytes(stockPath))?.Duration ?? TimeSpan.Zero;

            _floor = WaveFile.Describe(WaveFile.Trimmed(mine))?.Duration ?? now;

            Seconds.Minimum = Math.Round(_floor.TotalSeconds, 1);
            Seconds.Maximum = GameMedia.LongestActHold.TotalSeconds;
            Seconds.Value = Math.Clamp(Math.Round(now.TotalSeconds, 1), Seconds.Minimum, Seconds.Maximum);

            ShowValue();

            var overridden = _project?.HasOverride(_fanfare.Path) == true;
            ResetButton.IsEnabled = overridden;

            State.Text =
                $"The game's own is {stock.TotalSeconds:0.#} seconds."
                + (overridden ? $" This mod holds it for {now.TotalSeconds:0.#}." : "")
                + $" The shortest card is {_floor.TotalSeconds:0.#} seconds.";
        }
        finally
        {
            _loading = false;
        }
    }

    private void ShowValue() => Readout.Text = $"{Seconds.Value:0.#} seconds";

    private void OnSecondsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        ShowValue();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (_fanfare is null) return;
        if (_project is null)
        {
            Ui.Error(Owner, "No project open", "Create or open a mod project first.");
            return;
        }

        try
        {
            // Built from whatever the fanfare is now, trimmed first, so adjusting twice does
            // not stack silence on silence.
            var source = _project.HasOverride(_fanfare.Path)
                ? _project.ResolveContentPath(_fanfare.Path)
                : _session.StockFile(_fanfare.Path);

            var wanted = TimeSpan.FromSeconds(Seconds.Value);

            _session.FileUndo.Record(_project, $"act card timing {wanted.TotalSeconds:0.#}s",
                new[] { _fanfare.Path },
                () => _project.WriteOverride(_fanfare.Path,
                    WaveFile.HeldFor(File.ReadAllBytes(source), wanted)),
                refresh: Redraw);

            Status?.Invoke($"Act title cards now stay {wanted.TotalSeconds:0.#} seconds.");

            OverridesChanged?.Invoke();
            Load();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "set that", ex);
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_fanfare is null || _project is null || !_project.HasOverride(_fanfare.Path)) return;

        if (!Ui.ConfirmReset(Owner, "the act title card timing",
                "The game's own fanfare comes back, and its length with it."))
            return;

        try
        {
            _session.FileUndo.Record(_project, "reset act card timing",
                new[] { _fanfare.Path },
                () => _project.RemoveOverride(_fanfare.Path),
                refresh: Redraw);

            Status?.Invoke("Act title cards are back to the game's own timing.");

            OverridesChanged?.Invoke();
            Load();
        }
        catch (Exception ex)
        {
            Ui.Failed(Owner, "reset that", ex);
        }
    }

    /// <summary>Redraws this pane, for undo: a change taken back that is still on
    /// screen as it was reads as an undo that did not work.</summary>
    private void Redraw()
    {
        Load();
        OverridesChanged?.Invoke();
    }
}
