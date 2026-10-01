using System.Text.RegularExpressions;

namespace Mavue.Repository.Tests;

/// <summary>
/// Guards CLAUDE.md §2: features must not be silently removed. Every bullet in the product
/// sections of docs/SPEC.md must appear as a row in docs/FEATURES.md, and only SPEC-listed
/// exclusions may carry the "Excluded" status.
/// </summary>
[Trait("Category", "Unit")]
public partial class FeatureTrackingTests
{
    // SPEC sections 3-25 describe features; 26+ are cross-cutting requirements and policy.
    private const int FirstFeatureSection = 3;
    private const int LastFeatureSection = 25;

    [GeneratedRegex(@"^# (\d+)\. ")]
    private static partial Regex SectionHeading();

    [GeneratedRegex(@"^\| F\d{2}\.\d{2} \| (.+?) \| (.+?) \|")]
    private static partial Regex FeatureRow();

    private static List<string> SpecFeatureBullets()
    {
        var bullets = new List<string>();
        int section = 0;
        foreach (string line in File.ReadLines(Repo.PathOf("docs", "SPEC.md")))
        {
            Match heading = SectionHeading().Match(line);
            if (heading.Success)
            {
                section = int.Parse(heading.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            if (section is >= FirstFeatureSection and <= LastFeatureSection && line.StartsWith("- ", StringComparison.Ordinal))
            {
                bullets.Add(line[2..].Trim());
            }
        }

        return bullets;
    }

    private static List<(string Feature, string Status)> FeatureRows() =>
        File.ReadLines(Repo.PathOf("docs", "FEATURES.md"))
            .Select(l => FeatureRow().Match(l))
            .Where(m => m.Success)
            .Select(m => (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()))
            .ToList();

    [Fact]
    public void EverySpecFeatureIsTracked()
    {
        var tracked = FeatureRows().Select(r => r.Feature).ToHashSet(StringComparer.Ordinal);
        var missing = SpecFeatureBullets().Where(b => !tracked.Contains(b)).Distinct().ToList();

        Assert.True(missing.Count == 0, "SPEC features missing from docs/FEATURES.md:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void OnlySpecExclusionsAreMarkedExcluded()
    {
        string spec = File.ReadAllText(Repo.PathOf("docs", "SPEC.md"));
        string exclusions = spec[spec.IndexOf("# 29. Explicit Exclusions", StringComparison.Ordinal)..spec.IndexOf("# 30.", StringComparison.Ordinal)];

        var wronglyExcluded = FeatureRows()
            .Where(r => r.Status.StartsWith("Excluded", StringComparison.Ordinal) || r.Status.StartsWith("Backlog", StringComparison.Ordinal))
            .Where(r => !exclusions.Contains("- " + r.Feature, StringComparison.Ordinal))
            .Select(r => r.Feature)
            .ToList();

        Assert.True(wronglyExcluded.Count == 0, "Marked excluded/backlog but not excluded by SPEC §29:\n" + string.Join("\n", wronglyExcluded));
    }

    [Fact]
    public void StatusesUseTheAgreedVocabulary()
    {
        string[] allowed = ["Planned", "Investigating", "In Progress", "Implemented", "Tested", "Blocked", "Excluded (SPEC §29)", "Backlog (SPEC §16)"];
        var invalid = FeatureRows().Where(r => !allowed.Contains(r.Status)).Select(r => $"{r.Feature}: {r.Status}").ToList();

        Assert.True(invalid.Count == 0, string.Join("\n", invalid));
    }
}
