using Windows.Management.Deployment;

namespace Mavue.Shell;

/// <summary>
/// Registers or removes the signed identity package (see <see cref="IdentityPackageManifest"/>) for the current user,
/// with the Quick View host's folder as its external location. Uses the Windows PackageManager API, as in Microsoft
/// Learn "Grant package identity by packaging with external location manually" (per-user registration).
/// No administrator rights are needed; the signing certificate must be trusted (e.g. in CurrentUser\TrustedPeople).
/// </summary>
public sealed class ModernContextMenuRegistration
{
    private readonly string _packageName;

    public ModernContextMenuRegistration(string packageName = IdentityPackageManifest.PackageName)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageName);
        _packageName = packageName;
    }

    /// <summary>Full names of this package registered for the current user (normally zero or one).</summary>
    public IReadOnlyList<string> RegisteredPackages()
    {
        var manager = new PackageManager();
        return manager.FindPackagesForUser(string.Empty)
            .Where(p => string.Equals(p.Id.Name, _packageName, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id.FullName)
            .ToArray();
    }

    public bool IsRegistered => RegisteredPackages().Count > 0;

    /// <summary>Registers <paramref name="packagePath"/> (signed .msix) with <paramref name="externalLocation"/>, replacing an earlier registration.</summary>
    public async Task RegisterAsync(string packagePath, string externalLocation)
    {
        if (!Path.IsPathFullyQualified(packagePath) || !File.Exists(packagePath))
        {
            throw new ArgumentException("The identity package must be an existing, fully qualified .msix path.", nameof(packagePath));
        }

        if (!Path.IsPathFullyQualified(externalLocation) || !Directory.Exists(externalLocation) ||
            !File.Exists(Path.Combine(externalLocation, IdentityPackageManifest.CommandDll)) ||
            !File.Exists(Path.Combine(externalLocation, IdentityPackageManifest.HostExecutable)))
        {
            throw new ArgumentException($"The external location must contain {IdentityPackageManifest.HostExecutable} and {IdentityPackageManifest.CommandDll}.", nameof(externalLocation));
        }

        await UnregisterAsync().ConfigureAwait(false); // the same version cannot be registered twice

        var manager = new PackageManager();
        var options = new AddPackageOptions { ExternalLocationUri = new Uri(externalLocation.TrimEnd('\\') + "\\") };
        DeploymentResult result = await manager.AddPackageByUriAsync(new Uri(packagePath), options);
        if (result.ExtendedErrorCode is { } error && error.HResult != 0)
        {
            throw new InvalidOperationException($"Package registration failed (0x{error.HResult:X8}): {result.ErrorText}");
        }
    }

    /// <summary>Removes every registered version of the package. Returns how many were removed.</summary>
    public async Task<int> UnregisterAsync()
    {
        var manager = new PackageManager();
        int removed = 0;
        foreach (string fullName in RegisteredPackages())
        {
            DeploymentResult result = await manager.RemovePackageAsync(fullName);
            if (result.ExtendedErrorCode is { } error && error.HResult != 0)
            {
                throw new InvalidOperationException($"Package removal failed (0x{error.HResult:X8}): {result.ErrorText}");
            }

            removed++;
        }

        return removed;
    }
}
