using Windows.Security.Credentials;

namespace TinyAtc.Services;

/// <summary>
/// Stores the user's own credentials (API keys they paste in, OAuth refresh or session tokens) in
/// Windows Credential Manager via <see cref="PasswordVault"/>, encrypted per user. Values are
/// capped at about 1,200 characters (2,560 bytes as UTF-16); for anything larger, such as an OAuth
/// session carrying a DPoP key, encrypt a file in LocalFolder with DPAPI instead (see the skill's
/// storage reference).
/// </summary>
internal static class CredentialStore
{
    private const string ResourcePrefix = "TinyAtc.";

    public static string? Get(string name)
    {
        try
        {
            PasswordCredential credential = new PasswordVault().Retrieve(ResourcePrefix + name, name);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch
        {
            // Retrieve throws when nothing is stored; either way there is no value.
            return null;
        }
    }

    public static void Set(string name, string? value)
    {
        Remove(name);
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        try
        {
            new PasswordVault().Add(new PasswordCredential(ResourcePrefix + name, name, value));
        }
        catch
        {
            // The vault can be unavailable (e.g. roaming-profile issues); the user re-enters it.
        }
    }

    public static void Remove(string name)
    {
        try
        {
            PasswordVault vault = new();
            foreach (PasswordCredential credential in vault.FindAllByResource(ResourcePrefix + name))
            {
                vault.Remove(credential);
            }
        }
        catch
        {
            // FindAllByResource throws when nothing is stored.
        }
    }
}
