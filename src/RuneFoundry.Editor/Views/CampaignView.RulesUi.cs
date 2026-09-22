using System.IO;
using System.Windows;
using System.Windows.Controls;
using RuneFoundry.Core;
using RuneFoundry.Core.Formats;
using RuneFoundry.Core.Scenarios;
using RuneFoundry.UI;

namespace RuneFoundry.Editor.Views;

/// <summary>
/// The half of the Scenario rules card where an author writes their own.
///
/// A mission uses one of the game's rules or one of the author's, never both: two things
/// deciding the same mission is a question with no good answer, so picking a mode clears
/// the other.
/// </summary>
public partial class CampaignView
{
    private readonly List<ConditionRow> _victoryRows = new();
    private readonly List<ConditionRow> _defeatRows = new();
    private readonly List<GroupRow> _victoryGroups = new();
    private readonly List<GroupRow> _defeatGroups = new();

    // The top-level all-or-any for each set. They used to be pairs of radio buttons, which
    // meant the fact lived in the visual tree and fired a Checked handler while the XAML
    // was still loading. Now the only control for it is the word in the gutter.
    private Match _victoryMatch = Match.All;
    private Match _defeatMatch = Match.All;

    /// <summary>Whether the author has chosen to write the rule themselves.</summary>
    private bool WritingOwnRule => RuleOfMyOwn?.IsChecked == true;

    /// <summary>Fills both lists from the project, for the mission now selected.</summary>
    private void ShowRules()
    {
        if (_selected is null || VictoryList is null) return;

        var rules = _project?.ScenarioFor(_selected.Mission.ExeSlot);

        _loading = true;

        _victoryRows.Clear();
        _defeatRows.Clear();
        _victoryGroups.Clear();
        _defeatGroups.Clear();

        if (rules is not null)
        {
            foreach (var condition in rules.Victory.Conditions) _victoryRows.Add(Track(ConditionRow.From(condition)));
            foreach (var condition in rules.Defeat.Conditions) _defeatRows.Add(Track(ConditionRow.From(condition)));

            foreach (var group in rules.Victory.Nested) _victoryGroups.Add(TrackGroup(GroupRow.From(group, r => Track(r))));
            foreach (var group in rules.Defeat.Nested) _defeatGroups.Add(TrackGroup(GroupRow.From(group, r => Track(r))));

            _victoryMatch = rules.Victory.Match;
            _defeatMatch = rules.Defeat.Match;
        }
        else
        {
            _victoryMatch = Match.All;
            _defeatMatch = Match.All;
        }

        // The mode follows what is stored rather than what was last clicked, so moving
        // between missions shows each one as it is.
        if (rules is not null) RuleOfMyOwn.IsChecked = true;
        else RuleFromGame.IsChecked = true;

        _loading = false;

        RefreshRuleLists();
        ShowRuleMode();
    }

    private ConditionRow Track(ConditionRow row)
    {
        row.PropertyChanged += (_, _) => SaveRules();
        return row;
    }

    private GroupRow TrackGroup(GroupRow group)
    {
        group.PropertyChanged += (_, _) => SaveRules();
        return group;
    }

    /// <summary>
    /// Puts the joining word down the left of every list, and wires each one to change the
    /// match of the set it sits in.
    ///
    /// Nothing on the first row of a set, since it is joined to nothing; "and" or "or" on
    /// every row after it, which is where a reader would reach to change it. A group sits
    /// after the plain rows of its set, so it leads the set only when there is nothing
    /// above it, and its own rows are joined by its own match.
    ///
    /// Guarded, because every row reports its own changes so a mission saves as it is
    /// edited, and a word written here is not an edit anybody made. The same guard is what
    /// stops a change from fanning out: choosing a word on one row sets the match, this
    /// runs, and every sibling is rewritten while the callbacks are switched off.
    /// </summary>
    private void SetJoins()
    {
        var was = _loading;
        _loading = true;

        Apply(_victoryRows, _victoryMatch, ChangeVictory);
        Apply(_defeatRows, _defeatMatch, ChangeDefeat);

        Lead(_victoryGroups, _victoryMatch, _victoryRows.Count, ChangeVictory);
        Lead(_defeatGroups, _defeatMatch, _defeatRows.Count, ChangeDefeat);

        foreach (var group in _victoryGroups.Concat(_defeatGroups))
        {
            var owner = group;
            Apply(owner.Rows, owner.Match, m => { owner.Match = m; SetJoins(); SaveRules(); });
        }

        _loading = was;

        static void Apply(List<ConditionRow> rows, Match match, Action<Match> change)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i].SetMatch = null;
                rows[i].ShowJoin = i > 0;
                rows[i].JoinChoice = ConditionRow.JoinFor(match);
                rows[i].SetMatch = change;
            }
        }

        static void Lead(List<GroupRow> groups, Match match, int above, Action<Match> change)
        {
            for (var i = 0; i < groups.Count; i++)
            {
                groups[i].SetMatch = null;
                groups[i].ShowJoin = i > 0 || above > 0;
                groups[i].JoinChoice = ConditionRow.JoinFor(match);
                groups[i].SetMatch = change;
            }
        }
    }

    private void ChangeVictory(Match match)
    {
        if (_loading) return;
        _victoryMatch = match;
        SetJoins();
        SaveRules();
    }

    private void ChangeDefeat(Match match)
    {
        if (_loading) return;
        _defeatMatch = match;
        SetJoins();
        SaveRules();
    }

    /// <summary>
    /// Everything the rules say about themselves: the whole rule as one sentence.
    ///
    /// Run after any change, from the rebuild and from the save. Guarded the same way as
    /// SetJoins and for the same reason: a row reports what is written to it, and none of
    /// this is an edit.
    /// </summary>
    private void Describe()
    {
        if (VictorySentence is null) return;

        var was = _loading;
        _loading = true;

        var won = Clause(_victoryRows, _victoryGroups, _victoryMatch);
        VictorySentence.Text = won.Length == 0
            ? "Nothing decides a win yet. Add a condition."
            : "You win when " + won + ".";

        var lost = Clause(_defeatRows, _defeatGroups, _defeatMatch);
        DefeatSentence.Text = lost.Length == 0
            ? "You lose by the game's own rule: when you have nothing left."
            : "You lose when " + lost + ".";

        _loading = was;
    }

    /// <summary>
    /// Rows and groups as one clause. "and" or "or" between them by the set's match; a
    /// group reads as "either A or B" or "both A and B", which is the bracket said aloud.
    /// </summary>
    private static string Clause(List<ConditionRow> rows, List<GroupRow> groups, Match match)
    {
        var parts = rows.Select(r => r.Summary).Where(t => t.Length > 0).ToList();

        foreach (var group in groups)
        {
            var inner = group.Rows.Select(r => r.Summary).Where(t => t.Length > 0).ToList();
            if (inner.Count == 0) continue;

            var word = group.Match == Match.Any ? " or " : " and ";
            var lead = inner.Count < 2 ? ""
                : group.Match == Match.Any ? "either "
                : inner.Count == 2 ? "both " : "all of ";

            parts.Add(lead + string.Join(word, inner));
        }

        return string.Join(match == Match.Any ? ", or " : ", and ", parts);
    }


    private void RefreshRuleLists()
    {
        // The all-or-any buttons raise Checked as the XAML sets them, which happens while
        // InitializeComponent is still running and before any of these fields is assigned.
        // Anything reached from that handler has to survive being called against a view
        // that does not exist yet.
        // Every one of them, not just the first: the fields are assigned as their elements
        // are created, so the earlier lists exist while the later ones are still null.
        if (VictoryList is null || DefeatList is null
            || VictoryGroups is null || DefeatGroups is null) return;

        SetJoins();

        VictoryList.ItemsSource = null;
        VictoryList.ItemsSource = _victoryRows;

        DefeatList.ItemsSource = null;
        DefeatList.ItemsSource = _defeatRows;

        VictoryGroups.ItemsSource = null;
        VictoryGroups.ItemsSource = _victoryGroups;

        DefeatGroups.ItemsSource = null;
        DefeatGroups.ItemsSource = _defeatGroups;

        Describe();
    }


    private void OnAddVictoryGroup(object sender, RoutedEventArgs e)
    {
        // Opens as "any", because a group whose parent is "all" is almost always the or.
        _victoryGroups.Add(TrackGroup(new GroupRow { Match = Match.Any }));
        RefreshRuleLists();
        SaveRules();
    }

    private void OnAddDefeatGroup(object sender, RoutedEventArgs e)
    {
        _defeatGroups.Add(TrackGroup(new GroupRow { Match = Match.Any }));
        RefreshRuleLists();
        SaveRules();
    }

    private void OnAddToGroup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupRow group) return;

        group.Rows.Add(Track(new ConditionRow()));
        RefreshRuleLists();
        SaveRules();
    }

    private void OnRemoveGroup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupRow group) return;

        _victoryGroups.Remove(group);
        _defeatGroups.Remove(group);

        RefreshRuleLists();
        SaveRules();
    }

    /// <summary>Shows the half of the card the chosen mode needs, and hides the other.</summary>
    private void ShowRuleMode()
    {
        if (OwnRulesPanel is null) return;

        var own = WritingOwnRule;

        OwnRulesPanel.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
        ObjectiveBox.Visibility = own ? Visibility.Collapsed : Visibility.Visible;

        // The three notes under the game's-rule dropdown belong to that mode. Emptied but
        // left visible, each still took a line and its margin, and the three together were
        // the blank band that sat above the author's own rules.
        var notes = own ? Visibility.Collapsed : Visibility.Visible;
        ObjectiveAlso.Visibility = notes;
        ObjectiveNeeds.Visibility = notes;
        ObjectiveWarning.Visibility = notes;

        if (own)
        {
            DeliveryPanel.Visibility = Visibility.Collapsed;
            ObjectiveAlso.Text = "";
            ObjectiveNeeds.Text = "";
        }

        ShowRuleFindings();
    }

    private void OnRuleModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected is null || _project is null) return;

        ShowRuleMode();

        if (WritingOwnRule)
        {
            // A slot cannot answer to both, so choosing your own drops the game's.
            if (_project.ObjectiveFor(_selected.Mission.ExeSlot) is not null)
            {
                _project.SetObjective(_selected.Mission.ExeSlot, null);
                RefreshNotes();
            }

            SaveRules();
            return;
        }

        if (_project.SetScenario(_selected.Mission.ExeSlot, null))
        {
            _victoryRows.Clear();
            _defeatRows.Clear();
            RefreshRuleLists();
            RefreshNotes();

            Status?.Invoke("This mission uses the game's own rule again.");
        }
    }

    private void OnAddVictory(object sender, RoutedEventArgs e)
    {
        _victoryRows.Add(Track(new ConditionRow()));
        RefreshRuleLists();
        SaveRules();
    }

    private void OnAddDefeat(object sender, RoutedEventArgs e)
    {
        _defeatRows.Add(Track(new ConditionRow()));
        RefreshRuleLists();
        SaveRules();
    }

    private void OnRemoveCondition(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConditionRow row) return;

        _victoryRows.Remove(row);
        _defeatRows.Remove(row);

        foreach (var group in _victoryGroups.Concat(_defeatGroups)) group.Rows.Remove(row);

        RefreshRuleLists();
        SaveRules();
    }

    /// <summary>Writes both lists into the project, and says what is wrong with them.</summary>
    private void SaveRules()
    {
        if (_loading || _selected is null || _project is null || !WritingOwnRule) return;

        var rules = new ScenarioRules(
            new ConditionSet(
                _victoryMatch,
                _victoryRows.Select(r => r.ToCondition()).ToList(),
                _victoryGroups.Select(g => g.ToSet()).ToList()),
            new ConditionSet(
                _defeatMatch,
                _defeatRows.Select(r => r.ToCondition()).ToList(),
                _defeatGroups.Select(g => g.ToSet()).ToList()));

        _project.SetScenario(_selected.Mission.ExeSlot, rules);

        RefreshNotes();
        ShowRuleFindings();
        Describe();
    }

    /// <summary>
    /// Reads the rules against the map they will run on, and shows what will go wrong.
    ///
    /// Here rather than only at build time because this is where it can still be fixed. A
    /// rule met on the first tick and a rule that can never be met both look like a broken
    /// mission from inside the game.
    /// </summary>
    private void ShowRuleFindings()
    {
        if (RuleFindings is null) return;

        if (!WritingOwnRule || _selected is null)
        {
            RuleFindings.ItemsSource = null;
            return;
        }

        var rules = _project?.ScenarioFor(_selected.Mission.ExeSlot);
        if (rules is null)
        {
            RuleFindings.ItemsSource = null;
            return;
        }

        PudFile? map = null;
        try
        {
            if (ReadMap() is { } bytes) map = PudFile.Parse(bytes.Bytes);
        }
        catch (Exception)
        {
            // A map we cannot read is one we cannot check against. The rules still stand.
        }

        RuleFindings.ItemsSource = ScenarioValidator.Validate(rules, map);
    }

    /// <summary>
    /// Everything wrong with every mission's rules, for the build to refuse on.
    ///
    /// Shipping a mission that cannot be won is the failure this catches, and the build is
    /// the last moment anyone looks before other people play it.
    /// </summary>
    public static IReadOnlyList<string> ProblemsInRules(ModProject project, Session session)
    {
        var problems = new List<string>();

        foreach (var (slot, rules) in project.Scenarios)
        {
            PudFile? map = null;

            try
            {
                var mission = Campaign.All
                    .SelectMany(c => c.Missions(null))
                    .FirstOrDefault(m => m.ExeSlot == slot);

                if (mission is not null)
                {
                    var path = session.EffectiveFile(project, mission.MapPath);
                    if (path is not null && File.Exists(path)) map = PudFile.Load(path);
                }
            }
            catch (Exception)
            {
                // Checked without the map instead, which still catches the worst of it.
            }

            foreach (var finding in ScenarioValidator.Validate(rules, map))
                if (finding.Level == FindingLevel.Problem)
                    problems.Add($"Mission slot {slot}: {finding.Message}");
        }

        return problems;
    }
}
