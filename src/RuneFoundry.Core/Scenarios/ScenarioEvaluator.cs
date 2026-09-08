namespace RuneFoundry.Core.Scenarios;

/// <summary>
/// Decides whether the author's conditions hold, from one look at the game.
///
/// Pure: a snapshot in, a verdict out. Everything about when to look, how often, and what
/// to do about the answer belongs to the engine.
///
/// <para>
/// Every result is a <c>bool?</c>. Null means the value could not be read, and null is
/// never true. Ending somebody's mission because a read failed is the one mistake that
/// cannot be taken back, so a condition has to be shown to hold rather than merely fail to
/// be shown false.
/// </para>
/// </summary>
public static class ScenarioEvaluator
{
    /// <summary>
    /// Whether a set is satisfied.
    ///
    /// For <see cref="Match.All"/>, one unreadable condition makes the whole set unknown
    /// rather than false: the others being true says nothing about the one we could not see.
    /// For <see cref="Match.Any"/>, one readable true is enough, and unknowns only matter
    /// when nothing else was true.
    /// </summary>
    public static bool? Evaluate(ConditionSet set, IGameSnapshot game, int localPlayer,
                                 IReadOnlySet<int>? latched = null)
    {
        if (set.IsEmpty) return false;

        var unknown = false;

        for (var i = 0; i < set.Conditions.Count; i++)
        {
            if (latched?.Contains(i) == true)
            {
                if (set.Match == Match.Any) return true;
                continue;
            }

            var met = Evaluate(set.Conditions[i], game, localPlayer);

            if (met is null) { unknown = true; continue; }

            if (set.Match == Match.All && met == false) return false;
            if (set.Match == Match.Any && met == true) return true;
        }

        if (unknown) return null;
        return set.Match == Match.All;
    }

    /// <summary>Whether one condition holds.</summary>
    public static bool? Evaluate(Condition condition, IGameSnapshot game, int localPlayer)
    {
        var met = Test(condition, game, localPlayer);
        return met is null ? null : met.Value ^ condition.Invert;
    }

    private static bool? Test(Condition condition, IGameSnapshot game, int localPlayer)
    {
        var player = condition.Player ?? localPlayer;

        switch (condition.Kind)
        {
            case ConditionKind.OwnCount:
            {
                if (Count(condition, game, player) is not { } held) return null;
                return Compare(held, condition.Op, condition.Count);
            }

            case ConditionKind.UnitAlive:
            {
                if (Count(condition, game, player) is not { } alive) return null;
                return alive > 0;
            }

            case ConditionKind.EnemyHasNone:
            {
                // Every player but the local one and the neutral slot. A single unreadable
                // player makes the answer unknown, because the one we could not read is
                // exactly the one that might still hold something.
                for (var other = 0; other < GameAddresses.PlayerCount; other++)
                {
                    if (other == localPlayer || other == ScenarioValidator.NeutralPlayer) continue;

                    if (Count(condition, game, other) is not { } held) return null;
                    if (held > 0) return false;
                }

                return true;
            }

            case ConditionKind.PlayerEliminated:
                return Eliminated(game, player);

            case ConditionKind.Delivered:
            {
                if (game.Counter(GameAddresses.Delivered, player) is not { } packed) return null;

                // A delivery adds one; a hero adds a further 0x1000. So the two counts sit
                // in one word and come apart cleanly.
                var delivered = packed & 0xFFF;
                var heroes = packed / GameAddresses.HeroWeight;

                return Compare(delivered, condition.Op, condition.Count)
                       && heroes >= condition.Heroes;
            }

            case ConditionKind.Rescued:
                return game.Counter(GameAddresses.Rescued, player) is { } rescued
                    ? Compare(rescued, condition.Op, condition.Count)
                    : null;

            case ConditionKind.Kills:
                return game.Counter(GameAddresses.Kills, player) is { } kills
                    ? Compare(kills, condition.Op, condition.Count)
                    : null;

            case ConditionKind.Razings:
                return game.Counter(GameAddresses.Razings, player) is { } razed
                    ? Compare(razed, condition.Op, condition.Count)
                    : null;

            case ConditionKind.Resource:
            {
                if (ResourceCatalog.Find(condition.Counter) is not { } resource) return null;
                if (game.Resource(resource.Total, player) is not { } held) return null;

                return Compare((int)held, condition.Op, condition.Count);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// The game's own test for a player being out, at 0x004F4316: no buildings standing,
    /// and nothing left that counts as an army once the things that cannot take a town are
    /// taken off.
    ///
    /// Mirrored rather than approximated. A rule that says "player 4 is eliminated" and the
    /// game's own defeat check disagreeing about the same player would be very hard for an
    /// author to make sense of.
    /// </summary>
    private static bool? Eliminated(IGameSnapshot game, int player)
    {
        var buildings = game.Counter(GameAddresses.BuildingsAlive, player);
        var units = game.Counter(GameAddresses.UnitsAlive, player);
        var flyers = game.Counter(GameAddresses.Flyers, player);
        var transports = game.Counter(GameAddresses.Transports, player);
        var tankers = game.Counter(GameAddresses.Tankers, player);

        if (buildings is null || units is null || flyers is null
            || transports is null || tankers is null)
            return null;

        if (buildings > 0) return false;

        return units - flyers - transports - tankers <= 0;
    }

    private static int? Count(Condition condition, IGameSnapshot game, int player)
    {
        if (CounterCatalog.Find(condition.Counter) is not { } counter) return null;

        // Always the plain tally.
        //
        // The game keeps a second, ACTIVE table, and it does not mean "finished". It exists
        // only for the combat types, so no building has one at all, and read against a live
        // game it says zero while footmen are standing on the map. A rule asking for it
        // could never be met, which is exactly the failure that is impossible to see from
        // inside a mission.
        //
        // The cost is that a building counts from the moment its foundation is laid. The
        // game offers nothing better, and counting one building early beats a rule that
        // never fires.
        return game.Counter(counter.Total, player);
    }

    private static bool Compare(int actual, Compare op, int wanted) => op switch
    {
        Scenarios.Compare.AtLeast => actual >= wanted,
        Scenarios.Compare.AtMost => actual <= wanted,
        _ => actual == wanted,
    };
}
