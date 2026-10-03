using TinyAtc.OpenSky;
using Windows.Security.Credentials;

namespace TinyAtc.Services;

/// <summary>
/// The user's own OpenSky API client (ID and secret), kept in Windows Credential Manager through
/// <see cref="PasswordVault"/>: the ID as the user name, the secret as the password. Never in
/// LocalSettings, never logged.
/// </summary>
internal static class OpenSkyAccount
{
    private const string Resource = "TinyAtc.OpenSky";

    public static OpenSkyCredentials? Load()
    {
        try
        {
            PasswordVault vault = new();
            // FindAllByResource throws when nothing is stored.
            PasswordCredential credential = vault.FindAllByResource(Resource)[0];
            credential.RetrievePassword();
            return string.IsNullOrWhiteSpace(credential.UserName) || string.IsNullOrWhiteSpace(credential.Password)
                ? null
                : new OpenSkyCredentials(credential.UserName, credential.Password);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(OpenSkyCredentials credentials)
    {
        Remove();
        new PasswordVault().Add(new PasswordCredential(Resource, credentials.ClientId, credentials.ClientSecret));
    }

    public static void Remove()
    {
        try
        {
            PasswordVault vault = new();
            foreach (PasswordCredential credential in vault.FindAllByResource(Resource))
            {
                vault.Remove(credential);
            }
        }
        catch
        {
            // Nothing stored.
        }
    }
}
