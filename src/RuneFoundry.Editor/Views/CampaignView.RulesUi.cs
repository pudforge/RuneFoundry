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

            VictoryAll.IsChecked = rules.Victory.Match == Match.All;
            VictoryAny.IsChecked = rules.Victory.Match == Match.Any;
            DefeatAll.IsChecked = rules.Defeat.Match == Match.All;
            DefeatAny.IsChecked = rules.Defeat.Match == Match.Any;
        }
        else
        {
            VictoryAll.IsChecked = true;
            DefeatAll.IsChecked = true;
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

    private void RefreshRuleLists()
    {
        VictoryList.ItemsSource = null;
        VictoryList.ItemsSource = _victoryRows;

        DefeatList.ItemsSource = null;
        DefeatList.ItemsSource = _defeatRows;

        VictoryGroups.ItemsSource = null;
        VictoryGroups.ItemsSource = _victoryGroups;

        DefeatGroups.ItemsSource = null;
        DefeatGroups.ItemsSource = _defeatGroups;
    }

    private void OnMatchChanged(object sender, RoutedEventArgs e) => SaveRules();

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
                VictoryAny.IsChecked == true ? Match.Any : Match.All,
                _victoryRows.Select(r => r.ToCondition()).ToList(),
                _victoryGroups.Select(g => g.ToSet()).ToList()),
            new ConditionSet(
                DefeatAny.IsChecked == true ? Match.Any : Match.All,
                _defeatRows.Select(r => r.ToCondition()).ToList(),
                _defeatGroups.Select(g => g.ToSet()).ToList()));

        _project.SetScenario(_selected.Mission.ExeSlot, rules);

        RefreshNotes();
        ShowRuleFindings();
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
