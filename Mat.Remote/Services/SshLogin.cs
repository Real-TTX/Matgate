using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace Matgate.Services;

// How Matgate signs in to an SSH server - for the terminal and for SFTP alike: with the private key when the
// connection (or its credentials) has one, its password then being the key's passphrase; and with the password
// itself as well, as an SSH client tries one way after the other.
public static class SshLogin
{
    public static ConnectionInfo For(string host, int port, string user, string password, string privateKey)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(privateKey))
        {
            methods.Add(new PrivateKeyAuthenticationMethod(user, ReadKey(privateKey, password)));
        }

        if (!string.IsNullOrEmpty(password) || methods.Count == 0)
        {
            var keyboard = new KeyboardInteractiveAuthenticationMethod(user);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = password;
                }
            };
            methods.Add(new PasswordAuthenticationMethod(user, password));
            methods.Add(keyboard);
        }

        return new ConnectionInfo(host, port, user, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    // A key as it is pasted (OpenSSH, PEM/PKCS#1/PKCS#8 or PuTTY's .ppk). An encrypted key opens with the
    // password; one that is not encrypted ignores it.
    public static PrivateKeyFile ReadKey(string privateKey, string passphrase)
    {
        var text = privateKey.Replace("\r\n", "\n").Trim() + "\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        try
        {
            return string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            throw new SshAuthenticationException("The private key is encrypted and needs its passphrase as the password.");
        }
    }

    // Whether a pasted key can be read at all (the passphrase is not checked here: it may be filled in later).
    public static bool LooksLikeKey(string privateKey) =>
        privateKey.Contains("PRIVATE KEY-----", StringComparison.Ordinal)
        || privateKey.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal);
}
