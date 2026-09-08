using System.Windows;
using System.Windows.Controls;
using RuneFoundry.Core.Formats;

namespace RuneFoundry.UI;

/// <summary>
/// Pasting a whole AI script in as text.
///
/// Scripts are the one thing in a mod with no way in and no way out: art has files, strings
/// have a language file, but a script could only be built a row at a time in this editor.
/// Its text form already exists — the source view shows it and the assembler reads it back —
/// so this is that form with somewhere to put it.
///
/// The check happens as it is typed rather than on the way out. Two things can be wrong: the
/// text may not assemble, or it may assemble into more bytes than the slot holds, and both
/// are worth knowing before pressing a button rather than after.
/// </summary>
public partial class ScriptText : Window
{
    private readonly int _capacity;

    public ScriptText(string scriptName, int capacity, string current)
    {
        InitializeComponent();

        _capacity = capacity;

        Heading.Text = $"Replace {scriptName}";
        Note.Text =
            $"Paste a script in the form the editor shows. {scriptName} holds {capacity} bytes.";

        // Opening on what is there beats opening on nothing: it says what the format looks
        // like, and editing a copy is as reasonable a use of this as pasting a stranger's.
        Source.Text = current;
        Source.SelectAll();

        Loaded += (_, _) => Source.Focus();
        Check();
    }

    /// <summary>The script that was pasted, once it has been accepted.</summary>
    public List<AiInstruction>? Script { get; private set; }

    /// <summary>
    /// Asks for a script. Returns what was pasted, or null if the window was dismissed.
    /// </summary>
    /// <param name="capacity">Bytes the instructions may take.</param>
    public static List<AiInstruction>? Ask(Window? owner, string scriptName, int capacity, string current)
    {
        var window = new ScriptText(scriptName, capacity, current) { Owner = owner };
        return window.ShowDialog() == true ? window.Script : null;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => Check();

    /// <summary>
    /// Says whether what is in the box could be used, and why not when it could not.
    ///
    /// Assembling on every keystroke is affordable: the longest script in the game is 118
    /// instructions, and the parser is a switch over four opcodes.
    /// </summary>
    private void Check()
    {
        if (Verdict is null || Accept is null) return;

        var read = AiFile.CheckText(Source.Text, _capacity);

        if (!read.IsUsable)
        {
            Script = null;
            Say(read.Problem!, ok: false);
            return;
        }

        Script = read.Instructions;
        Say($"{read.Instructions!.Count} instructions, {read.Bytes} of {_capacity} bytes.", ok: true);
    }

    private void Say(string message, bool ok)
    {
        Verdict.Text = message;
        Verdict.SetResourceReference(ForegroundProperty, ok ? "Text" : "Warn");
        Accept.IsEnabled = ok;

        if (!ok) Script = null;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (Script is null) return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
