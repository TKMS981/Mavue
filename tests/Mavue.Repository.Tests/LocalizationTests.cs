using System.Xml.Linq;

namespace Mavue.Repository.Tests;

/// <summary>Japanese and English are both initial languages (CLAUDE.md §14); neither may lag behind.</summary>
[Trait("Category", "Unit")]
public class LocalizationTests
{
    private static readonly string[] Languages = ["en-US", "ja-JP"];

    public static TheoryData<string> ResourceProjects()
    {
        var data = new TheoryData<string>();
        foreach (string strings in Directory.GetDirectories(Repo.PathOf("src"), "Strings", SearchOption.AllDirectories))
        {
            data.Add(Path.GetRelativePath(Repo.Root, strings));
        }

        return data;
    }

    private static Dictionary<string, string> Load(string reswPath) =>
        XDocument.Load(reswPath).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    [Theory]
    [MemberData(nameof(ResourceProjects))]
    public void AllLanguagesDefineTheSameNonEmptyKeys(string stringsDir)
    {
        var sets = Languages.ToDictionary(
            lang => lang,
            lang => Load(Path.Combine(Repo.Root, stringsDir, lang, "Resources.resw")));

        var allKeys = sets.Values.SelectMany(s => s.Keys).ToHashSet(StringComparer.Ordinal);
        foreach (var (lang, entries) in sets)
        {
            Assert.Empty(allKeys.Except(entries.Keys).Select(k => $"{lang}: missing {k}"));
            Assert.Empty(entries.Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => $"{lang}: empty {e.Key}"));
        }
    }

    [Fact]
    public void ResourceProjectsExist()
    {
        Assert.NotEmpty(ResourceProjects());
    }
}
