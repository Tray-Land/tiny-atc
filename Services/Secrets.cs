using Microsoft.Windows.ApplicationModel.Resources;

namespace TinyAtc.Services;

/// <summary>
/// The app's own API keys, compiled into its resources from the gitignored OAuth.resw (copy
/// OAuth.resw.sample to create it). They ship inside the package, so only use this for keys that
/// are meant to live on the client (restricted maps keys, free-tier API keys). The user's own
/// OpenSky API client goes in <see cref="OpenSkyAccount"/>.
/// </summary>
internal static class Secrets
{
    private static readonly Lazy<string?> s_azureMapsKey = new(() => GetOAuthString("AzureMapsKey"));

    /// <summary>The Azure Maps key for the basemap, or null to fall back to OpenStreetMap tiles.</summary>
    public static string? AzureMapsKey => s_azureMapsKey.Value;

    private static string? GetOAuthString(string name)
    {
        try
        {
            ResourceLoader loader = new(ResourceLoader.GetDefaultResourceFilePath(), "OAuth");
            string value = loader.GetString(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            // OAuth.resw is missing from this build.
            return null;
        }
    }
}
