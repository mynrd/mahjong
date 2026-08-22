using Mahjong.Domain;
using Microsoft.Extensions.Configuration;

namespace Mahjong.Api.Tests;

/// <summary>
/// The house rules a new table starts with come from the Mahjong:Rules section rather than from
/// <see cref="RuleOptions.Default"/>, so these tests hold the wiring in place: that the shipped
/// appsettings.json really does bind onto every field including the two enum-keyed money tables,
/// that appsettings.Development.json wins where it repeats a key and leaves the rest alone, and
/// that a section listing only one value does not blank out everything it omits.
/// </summary>
public class RuleConfigurationTests
{
    private static readonly string SettingsDirectory =
        Path.Combine(AppContext.BaseDirectory, "AppSettings");

    private static RuleOptions Bind(params string[] environments)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(SettingsDirectory)
            .AddJsonFile("appsettings.json", optional: false);

        foreach (var environment in environments)
            builder.AddJsonFile($"appsettings.{environment}.json", optional: false);

        // A fresh instance, never RuleOptions.Default: Bind fills the object in place, so binding
        // onto the shared static would rewrite the compiled defaults for the rest of the run.
        var rules = new RuleOptions();
        builder.Build().GetSection("Mahjong:Rules").Bind(rules);
        return rules;
    }

    [Fact]
    public void AppSettingsBindsEveryRuleSwitch()
    {
        var rules = Bind();

        Assert.True(rules.JokerEnabled);
        Assert.False(rules.JokerCanCompleteClaimedWin);
        Assert.True(rules.SieteParesEnabled);
        Assert.True(rules.DistinctPairsForSietePares);
        Assert.True(rules.ChowFromLeftOnly);
        Assert.True(rules.TodasBeatsPungAndKang);
        Assert.Equal(6, rules.ClaimWindowSeconds);
        Assert.True(rules.ManoKeepsSeatOnDraw);
        Assert.Equal(5, rules.QuickWinDiscardLimit);
    }

    [Fact]
    public void AppSettingsBindsTheScoringProfile()
    {
        var scoring = Bind().Scoring;

        Assert.Equal(2, scoring.TodasBase);
        Assert.True(scoring.BunotDoubles);
        Assert.Equal(2, scoring.DiscarderMultiplier);

        // Enum-keyed dictionaries are the part most likely to bind to an empty table without
        // anyone noticing, so every key is checked rather than a sample.
        Assert.Equal(Enum.GetValues<Ambition>().Length, scoring.Ambitions.Count);
        Assert.Equal(1, scoring.Ambitions[Ambition.NoFlowers]);
        Assert.Equal(1, scoring.Ambitions[Ambition.Kang]);
        Assert.Equal(2, scoring.Ambitions[Ambition.SecretKang]);
        Assert.Equal(2, scoring.Ambitions[Ambition.Sagasa]);

        Assert.Equal(Enum.GetValues<WinBonus>().Length, scoring.Bonuses.Count);
        Assert.Equal(4, scoring.Bonuses[WinBonus.Escalera]);
        Assert.Equal(4, scoring.Bonuses[WinBonus.SietePares]);
        Assert.Equal(2, scoring.Bonuses[WinBonus.Flush]);
        Assert.Equal(2, scoring.Bonuses[WinBonus.AllPungs]);
        Assert.Equal(20, scoring.Bonuses[WinBonus.Bisaklat]);
    }

    /// <summary>
    /// The shipped file is meant to say out loud what the compiled defaults already were. If the
    /// two ever disagree, one of them was edited alone and the room a player actually sits at no
    /// longer matches RULES.md.
    /// </summary>
    [Fact]
    public void AppSettingsMatchesTheCompiledDefaults()
    {
        var bound = Bind();
        var expected = RuleOptions.Default;

        Assert.Equal(
            expected.Scoring.Ambitions.OrderBy(entry => entry.Key),
            bound.Scoring.Ambitions.OrderBy(entry => entry.Key));

        Assert.Equal(
            expected.Scoring.Bonuses.OrderBy(entry => entry.Key),
            bound.Scoring.Bonuses.OrderBy(entry => entry.Key));

        // The two money tables are compared above. Record equality would compare them by reference,
        // so they are swapped for the same instances before the rest of the fields are checked.
        Assert.Equal(expected, bound with
        {
            Scoring = bound.Scoring with
            {
                Ambitions = expected.Scoring.Ambitions,
                Bonuses = expected.Scoring.Bonuses,
            },
        });
    }

    [Fact]
    public void DevelopmentOverridesOnlyTheKeysItRepeats()
    {
        var rules = Bind("Development");

        Assert.Equal(3, rules.ClaimWindowSeconds);

        // Everything the Development file stays silent about keeps the base value.
        Assert.Equal(5, rules.QuickWinDiscardLimit);
        Assert.True(rules.JokerEnabled);
        Assert.Equal(2, rules.Scoring.TodasBase);
        Assert.Equal(20, rules.Scoring.Bonuses[WinBonus.Bisaklat]);
    }

    /// <summary>
    /// A section naming one bonus must not wipe the other eleven, or a table would start paying
    /// nothing for a hand the rules say is worth twenty.
    /// </summary>
    [Fact]
    public void PartialDictionarySectionKeepsTheUnlistedEntries()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mahjong:Rules:Scoring:Bonuses:Flush"] = "7",
            })
            .Build();

        var rules = new RuleOptions();
        config.GetSection("Mahjong:Rules").Bind(rules);

        Assert.Equal(7, rules.Scoring.Bonuses[WinBonus.Flush]);
        Assert.Equal(20, rules.Scoring.Bonuses[WinBonus.Bisaklat]);
        Assert.Equal(Enum.GetValues<WinBonus>().Length, rules.Scoring.Bonuses.Count);

        // The bound table must not have been the one every other table reads from.
        Assert.Equal(2, ScoringProfile.Default.Bonuses[WinBonus.Flush]);
        Assert.Equal(2, RuleOptions.Default.Scoring.Bonuses[WinBonus.Flush]);
    }
}
