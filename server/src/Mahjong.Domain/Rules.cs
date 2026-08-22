namespace Mahjong.Domain;

/// <summary>
/// Bonuses paid the moment they happen, in the middle of a hand, by all three other players.
/// Filipino tables call these "ambitions".
/// </summary>
public enum Ambition
{
    /// <summary>Dealt no flowers or seasons at all at the start of the hand.</summary>
    NoFlowers,

    /// <summary>Declared a kang off a discard.</summary>
    Kang,

    /// <summary>Declared a kang from four self-drawn tiles.</summary>
    SecretKang,

    /// <summary>Extended an already-exposed pung to a kang with a self-drawn fourth tile.</summary>
    Sagasa,
}

/// <summary>Bonuses added to the base win value and paid at the end of the hand.</summary>
public enum WinBonus
{
    /// <summary>A full 1-9 run in one suit, read as the three chows 123, 456 and 789.</summary>
    Escalera,

    /// <summary>Won on the seven-pairs-plus-one-set shape.</summary>
    SietePares,

    /// <summary>Nothing was ever exposed. Also called "all up".</summary>
    Concealed,

    /// <summary>Everything except the pair was exposed. Also called "all down".</summary>
    AllExposed,

    /// <summary>Every tile in the hand is from a single suit.</summary>
    Flush,

    /// <summary>All five bahay are pungs or kangs.</summary>
    AllPungs,

    /// <summary>All five bahay are chows.</summary>
    AllChows,

    /// <summary>Won on or before the fifth discard of the hand.</summary>
    QuickWin,

    /// <summary>Was waiting on exactly one tile.</summary>
    Single,

    /// <summary>Waiting on the middle tile of a run, e.g. holding 4 and 6 and needing 5.</summary>
    Paningit,

    /// <summary>Waiting on two pairs, either of which completes the hand.</summary>
    BackToBack,

    /// <summary>The mano's dealt hand was already complete before anyone discarded.</summary>
    Bisaklat,
}

/// <summary>
/// The money values for one table. Filipino house rules differ on every number here, so this is
/// stored per room as JSON rather than compiled in. Values are abstract "units"; the room decides
/// what a unit is worth.
/// </summary>
public sealed record ScoringProfile
{
    /// <summary>What a plain todas pays before any bonus.</summary>
    public int TodasBase { get; init; } = 2;

    /// <summary>Units each ambition pays, charged to each of the three other players.</summary>
    public IReadOnlyDictionary<Ambition, int> Ambitions { get; init; } = new Dictionary<Ambition, int>
    {
        [Ambition.NoFlowers] = 1,
        [Ambition.Kang] = 1,
        [Ambition.SecretKang] = 2,
        [Ambition.Sagasa] = 2,
    };

    /// <summary>Units each win bonus adds to the total.</summary>
    public IReadOnlyDictionary<WinBonus, int> Bonuses { get; init; } = new Dictionary<WinBonus, int>
    {
        [WinBonus.Escalera] = 4,
        [WinBonus.SietePares] = 4,
        [WinBonus.Concealed] = 1,
        [WinBonus.AllExposed] = 1,
        [WinBonus.Flush] = 2,
        [WinBonus.AllPungs] = 2,
        [WinBonus.AllChows] = 1,
        [WinBonus.QuickWin] = 1,
        [WinBonus.Single] = 1,
        [WinBonus.Paningit] = 1,
        [WinBonus.BackToBack] = 1,
        [WinBonus.Bisaklat] = 20,
    };

    /// <summary>Self-drawn win doubles the whole total and every player pays it.</summary>
    public bool BunotDoubles { get; init; } = true;

    /// <summary>How much more the player who fed the winning tile pays than the other two.</summary>
    public int DiscarderMultiplier { get; init; } = 2;

    public static readonly ScoringProfile Default = new();
}

/// <summary>
/// Rule switches for one table. Every flag here corresponds to a genuine disagreement between
/// published Filipino Mahjong rule sets, recorded in RULES.md section 7. Defaults follow the
/// majority reading.
/// </summary>
public sealed record RuleOptions
{
    /// <summary>Pick a random playable face each hand whose four copies are wild.</summary>
    public bool JokerEnabled { get; init; } = true;

    /// <summary>Whether a joker may stand in for the tile claimed off a discard to win.</summary>
    public bool JokerCanCompleteClaimedWin { get; init; } = false;

    /// <summary>Whether the seven-pairs-plus-one-set shape is a legal win at this table.</summary>
    public bool SieteParesEnabled { get; init; } = true;

    /// <summary>Whether the seven pairs must be seven different faces.</summary>
    public bool DistinctPairsForSietePares { get; init; } = true;

    /// <summary>Whether a chow may only be claimed from the player immediately before you.</summary>
    public bool ChowFromLeftOnly { get; init; } = true;

    /// <summary>Whether a todas declaration outranks a pung or kang on the same discard.</summary>
    public bool TodasBeatsPungAndKang { get; init; } = true;

    /// <summary>How long other players get to claim a discard before play moves on.</summary>
    public int ClaimWindowSeconds { get; init; } = 6;

    /// <summary>Whether the mano keeps the deal when a hand ends with no winner.</summary>
    public bool ManoKeepsSeatOnDraw { get; init; } = true;

    /// <summary>Discards after which a win no longer counts as a quick win.</summary>
    public int QuickWinDiscardLimit { get; init; } = 5;

    /// <summary>
    /// A fresh profile per table, not the shared <see cref="ScoringProfile.Default"/> instance.
    /// The configuration binder fills a nested object in place, so handing it the shared one would
    /// rewrite the defaults for every other table in the process.
    /// </summary>
    public ScoringProfile Scoring { get; init; } = new();

    public static readonly RuleOptions Default = new();
}
