using System.Reflection;

namespace Mavue.Repository.Tests;

internal static class Repo
{
    public static string Root { get; } = typeof(Repo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "RepositoryRoot").Value!;

    public static string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);
}
