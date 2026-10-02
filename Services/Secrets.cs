using Microsoft.Windows.ApplicationModel.Resources;

namespace TinyAtc.Services;

/// <summary>
/// The app's own API keys, compiled into its resources from the gitignored OAuth.resw (copy
/// OAuth.resw.sample to create it). They ship inside the package, so only use this for keys that
/// are meant to live on the client (restricted maps keys, free-tier API keys). A user's own
/// tokens go in <see cref="CredentialStore"/>.
/// </summary>
internal static class Secrets
{
    private static readonly Lazy<string?> s_exampleApiKey = new(() => GetOAuthString("ExampleApiKey"));

    /// <summary>The example service's API key, or null when OAuth.resw doesn't provide one.</summary>
    public static string? ExampleApiKey => s_exampleApiKey.Value;

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
