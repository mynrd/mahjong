using System.Collections;
using System.Reflection;
using Mahjong.Domain;
using Microsoft.Extensions.Configuration;

namespace Mahjong.Api.Tests;

/// <summary>
/// These hold the wiring between the settings files and <see cref="RuleOptions"/> in place: that
/// every key a file names really does reach a property, that the two enum-keyed money tables bind
/// whole instead of arriving empty, that an environment file overrides only the keys it repeats,
/// and that a section naming one entry does not wipe the rest.
///
/// What they deliberately do not do is assert the values in appsettings.json. Those are house
/// rules - data the table owner is meant to edit, which is the whole point of keeping them in
/// JSON rather than in code. A test that pinned them would turn every legitimate settings change
/// into a failing build, and the usual way that gets resolved is by editing the test to agree,
/// which teaches nobody anything. So the file is treated as the input it is: whatever it says
/// must bind, and whatever it leaves out must fall back to the compiled default. Change a number
/// in appsettings.json and these still pass. Misspell a key, and they do not.
/// </summary>
public class RuleConfigurationTests
{
    private static readonly string SettingsDirectory =
        Path.Combine(AppContext.BaseDirectory, "AppSettings");

    private const string RulesSection = "Mahjong:Rules";

    private static IConfigurationRoot Read(params string[] fileNames)
    {
        var builder = new ConfigurationBuilder().SetBasePath(SettingsDirectory);

        foreach (var fileName in fileNames)
            builder.AddJsonFile(fileName, optional: false);

        return builder.Build();
    }

    private static RuleOptions Bind(params string[] fileNames)
    {
        // A fresh instance, never RuleOptions.Default: Bind fills the object in place, so binding
        // onto the shared static would rewrite the compiled defaults for the rest of the run.
        var rules = new RuleOptions();
        Read(fileNames).GetSection(RulesSection).Bind(rules);
        return rules;
    }

    /// <summary>
    /// Every leaf key a file sets under Mahjong:Rules, relative to that section, with the "//"
    /// documentation keys dropped. One file is read alone, so a layered lookup cannot credit it
    /// with a key it does not actually set.
    /// </summary>
    private static Dictionary<string, string> SettingsIn(string fileName)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Walk(Read(fileName).GetSection(RulesSection), "");
        return found;

        void Walk(IConfiguration node, string prefix)
        {
            foreach (var child in node.GetChildren())
            {
                if (child.Key.StartsWith("//")) continue;

                var path = prefix.Length == 0 ? child.Key : $"{prefix}:{child.Key}";

                if (child.Value is not null) found[path] = child.Value;
                else Walk(child, path);
            }
        }
    }

    /// <summary>
    /// The bound object flattened onto the same "Scoring:Bonuses:Flush" paths the settings files
    /// use, so a file and the options it produced can be compared key by key without either side
    /// naming a value.
    /// </summary>
    private static Dictionary<string, string> Flatten(object root)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Walk(root, "");
        return found;

        void Walk(object node, string prefix)
        {
            foreach (var property in node.GetType()
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0) continue;

                var value = property.GetValue(node);
                if (value is null) continue;

                var path = prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}";

                // Dictionary<TEnum, int> is what the binder builds for the money tables, and it
                // implements the non-generic interface, so the enum key needs no reflection.
                if (value is IDictionary table)
                {
                    foreach (DictionaryEntry entry in table)
                        found[$"{path}:{entry.Key}"] = entry.Value?.ToString() ?? "";
                }
                else if (value.GetType().IsPrimitive || value is string)
                {
                    found[path] = value.ToString() ?? "";
                }
                else
                {
                    Walk(value, path);
                }
            }
        }
    }

    /// <summary>
    /// The failure this catches is a key that binds to nothing: renamed in <see cref="RuleOptions"/>
    /// but not in the JSON, or simply misspelt. Nothing throws when that happens - the table just
    /// quietly keeps the compiled default while the file appears to say otherwise.
    /// </summary>
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void EverySettingInAFileBindsOntoARuleProperty(string fileName)
    {
        var settings = SettingsIn(fileName);
        Assert.NotEmpty(settings);

        // Bound from that file alone, so whatever landed in the object came from it.
        var bound = Flatten(Bind(fileName));

        foreach (var (path, value) in settings)
        {
            Assert.True(
                bound.ContainsKey(path),
                $"{fileName} sets '{path}', which is not a rule property. A renamed or misspelt "
                    + "key binds to nothing and the table silently keeps the compiled default.");

            Assert.True(
                string.Equals(value, bound[path], StringComparison.OrdinalIgnoreCase),
                $"{fileName} sets '{path}' to '{value}' but the bound options hold '{bound[path]}'.");
        }
    }

    /// <summary>
    /// Enum-keyed dictionaries are the part most likely to bind to an empty table without anyone
    /// noticing, which would leave a hand paying nothing at all.
    /// </summary>
    [Fact]
    public void TheMoneyTablesBindWholeRatherThanEmpty()
    {
        var scoring = Bind("appsettings.json").Scoring;

        foreach (var ambition in Enum.GetValues<Ambition>())
            Assert.True(scoring.Ambitions.ContainsKey(ambition), $"{ambition} has no price.");

        foreach (var bonus in Enum.GetValues<WinBonus>())
            Assert.True(scoring.Bonuses.ContainsKey(bonus), $"{bonus} has no price.");
    }

    /// <summary>
    /// Anything a settings file leaves out falls back to the compiled default, so the fallback has
    /// to price everything. This is the half of the old "settings match the defaults" test worth
    /// keeping: it constrains the code, which is not the table owner's to edit, and says nothing
    /// about what the shipped JSON chose.
    /// </summary>
    [Fact]
    public void TheCompiledDefaultsPriceEveryAmbitionAndBonus()
    {
        foreach (var ambition in Enum.GetValues<Ambition>())
            Assert.True(
                RuleOptions.Default.Scoring.Ambitions.ContainsKey(ambition),
                $"{ambition} has no compiled default, so a file omitting it leaves it unpriced.");

        foreach (var bonus in Enum.GetValues<WinBonus>())
            Assert.True(
                RuleOptions.Default.Scoring.Bonuses.ContainsKey(bonus),
                $"{bonus} has no compiled default, so a file omitting it leaves it unpriced.");
    }

    /// <summary>
    /// The layering contract, stated without either file naming a value: whatever moved when
    /// Development was stacked on top, Development must have asked for by name, and everything it
    /// asked for must have actually taken effect.
    /// </summary>
    [Fact]
    public void DevelopmentOverridesOnlyTheKeysItRepeats()
    {
        var baseline = Flatten(Bind("appsettings.json"));
        var layered = Flatten(Bind("appsettings.json", "appsettings.Development.json"));
        var overrides = SettingsIn("appsettings.Development.json");

        foreach (var (path, value) in baseline)
        {
            if (string.Equals(layered[path], value, StringComparison.OrdinalIgnoreCase)) continue;

            Assert.True(
                overrides.ContainsKey(path),
                $"'{path}' changed to '{layered[path]}' when appsettings.Development.json was "
                    + "layered on, but that file never names it.");
        }

        foreach (var (path, value) in overrides)
            Assert.True(
                string.Equals(value, layered[path], StringComparison.OrdinalIgnoreCase),
                $"appsettings.Development.json sets '{path}' to '{value}' but the layered options "
                    + $"hold '{layered[path]}'.");
    }

    /// <summary>
    /// A section naming one bonus must not wipe the other eleven, or a table would start paying
    /// nothing for a hand the rules say is worth twenty.
    /// </summary>
    [Fact]
    public void PartialDictionarySectionKeepsTheUnlistedEntries()
    {
        // Derived from the default rather than written down, so this stays a test about binding
        // even if the table is repriced.
        var shared = ScoringProfile.Default.Bonuses[WinBonus.Flush];
        var sentinel = shared + 5;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mahjong:Rules:Scoring:Bonuses:Flush"] = sentinel.ToString(),
            })
            .Build();

        var rules = new RuleOptions();
        config.GetSection(RulesSection).Bind(rules);

        Assert.Equal(sentinel, rules.Scoring.Bonuses[WinBonus.Flush]);

        // Every bonus the section stayed silent about keeps whatever the default prices it at.
        foreach (var bonus in Enum.GetValues<WinBonus>().Where(bonus => bonus != WinBonus.Flush))
            Assert.Equal(ScoringProfile.Default.Bonuses[bonus], rules.Scoring.Bonuses[bonus]);

        // The bound table must not have been the one every other table reads from.
        Assert.Equal(shared, ScoringProfile.Default.Bonuses[WinBonus.Flush]);
        Assert.Equal(shared, RuleOptions.Default.Scoring.Bonuses[WinBonus.Flush]);
    }
}
