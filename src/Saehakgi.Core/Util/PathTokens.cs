namespace Saehakgi.Core.Util;

/// <summary>
/// Replaces well-known per-user path prefixes with environment tokens so paths
/// survive migration to a machine where the Windows account name differs.
/// e.g. C:\Users\alice\Documents  →  %USERPROFILE%\Documents
/// </summary>
public static class PathTokens
{
    public static string Tokenize(string absolutePath)
    {
        foreach (var (token, value) in Map())
        {
            if (!string.IsNullOrEmpty(value) &&
                absolutePath.StartsWith(value, StringComparison.OrdinalIgnoreCase))
            {
                return token + absolutePath.Substring(value.Length);
            }
        }
        return absolutePath;
    }

    public static string Expand(string tokenizedPath) =>
        Environment.ExpandEnvironmentVariables(tokenizedPath);

    private static IEnumerable<(string Token, string Value)> Map()
    {
        // Most specific first so LOCALAPPDATA/APPDATA win over USERPROFILE.
        yield return ("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        yield return ("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        yield return ("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }
}
