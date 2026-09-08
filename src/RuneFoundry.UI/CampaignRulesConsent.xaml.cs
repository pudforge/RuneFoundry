using System.Windows;

namespace RuneFoundry.UI;

/// <summary>
/// What the person is agreeing to before RuneFoundry writes into the running game.
///
/// This is the one thing the program does that carries a cost someone else pays: the game
/// is signed in to Battle.net and ships Blizzard's anti-cheat, so the exposure lands on the
/// player's account rather than on whoever built the mod. Doing that silently — which is
/// what the program did until this existed — is not a defensible default, however small the
/// risk turns out to be.
///
/// Written to be read, not clicked past: what the feature needs, what is unknowable about
/// it, what is actually written, and what happens if you say no. Saying no is a real option
/// that costs only this one feature, and the dialog says so.
/// </summary>
public partial class CampaignRulesConsent : Window
{
    private CampaignRulesConsent() => InitializeComponent();

    /// <summary>What the person decided.</summary>
    public enum Answer
    {
        /// <summary>Write the campaign rules into the running game.</summary>
        Allow,

        /// <summary>Apply the rest of the mod and leave the campaign alone.</summary>
        Skip,
    }

    /// <summary>
    /// Asks, unless they have already said to stop asking.
    ///
    /// The remembered answer lives in settings rather than per mod: it is a judgement about
    /// the person's own account, not about one author's work.
    /// </summary>
    public static Answer Ask(Window? owner, Settings settings)
    {
        if (settings.CampaignRulesConsent is "allow") return Answer.Allow;
        if (settings.CampaignRulesConsent is "skip") return Answer.Skip;

        var dialog = new CampaignRulesConsent { Owner = owner };
        dialog.ShowDialog();

        if (dialog.Remember.IsChecked == true)
        {
            settings.CampaignRulesConsent = dialog._answer == Answer.Allow ? "allow" : "skip";
            settings.Save();
        }

        return dialog._answer;
    }

    // Closing with the X is not consent, so the default is to skip.
    private Answer _answer = Answer.Skip;

    private void OnAllow(object sender, RoutedEventArgs e)
    {
        _answer = Answer.Allow;
        Close();
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        _answer = Answer.Skip;
        Close();
    }
}
