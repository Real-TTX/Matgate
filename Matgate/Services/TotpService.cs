using System.Security.Cryptography;
using System.Text;

namespace Matgate.Services;

// Der zweite Faktor nach RFC 6238: ein sechsstelliger Code, der sich alle 30 Sekunden ändert und
// aus einem gemeinsamen Geheimnis und der Uhrzeit entsteht. Die Authenticator-App rechnet dasselbe
// aus - übertragen wird dabei nichts.
//
// Zwei Dinge, die man leicht falsch macht und die hier bewusst anders gelöst sind:
//
//  * Die Uhren gehen auseinander. Geprüft wird deshalb nicht nur der laufende Zeitschritt, sondern
//    auch der davor und der danach - eine knappe Minute Spielraum. Mehr nicht: jeder weitere Schritt
//    vergrößert das Fenster, in dem ein abgefangener Code noch gilt.
//  * Ein Code gilt 30 Sekunden lang - also lange genug, um ihn ein zweites Mal einzutippen. Wer
//    einen Code mitliest, könnte ihn in dieser Zeit selbst verwenden. Deshalb merkt sich das Konto
//    den zuletzt benutzten Zeitschritt, und alles, was nicht neuer ist, wird abgewiesen.
public static class TotpService
{
    private const int Digits = 6;
    private const int StepSeconds = 30;
    private const int Window = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    // 20 Bytes = 160 Bit, die Länge, die RFC 4226 für HMAC-SHA1 vorsieht.
    public static string NewSecret()
    {
        return ToBase32(RandomNumberGenerator.GetBytes(20));
    }

    // Was die Authenticator-App einliest. Der Aussteller steht zweimal darin - einmal als Pfad,
    // einmal als Parameter -, weil die Apps sich nicht einig sind, welchen sie lesen.
    public static string Uri(string issuer, string account, string secret)
    {
        var label = System.Uri.EscapeDataString(issuer) + ":" + System.Uri.EscapeDataString(account);
        return $"otpauth://totp/{label}"
            + $"?secret={secret}"
            + $"&issuer={System.Uri.EscapeDataString(issuer)}"
            + $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    // Prüft einen Code und gibt den Zeitschritt zurück, zu dem er gehörte. Der Aufrufer schreibt
    // diesen Schritt ins Konto; beim nächsten Mal muss er echt größer sein.
    public static bool Verify(string? secret, string? code, long lastUsedStep, out long usedStep)
    {
        usedStep = 0;
        var sauber = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (sauber.Length != Digits || string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        byte[] schluessel;
        try
        {
            schluessel = FromBase32(secret);
        }
        catch (FormatException)
        {
            return false;
        }

        var jetzt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;
        for (var versatz = -Window; versatz <= Window; versatz++)
        {
            var schritt = jetzt + versatz;
            if (schritt <= lastUsedStep)
            {
                // Schon benutzt - oder älter als der zuletzt benutzte. Beides zählt nicht.
                continue;
            }

            if (FixedTimeEquals(Compute(schluessel, schritt), sauber))
            {
                usedStep = schritt;
                return true;
            }
        }

        return false;
    }

    // Wiederherstellungs-Codes für den Fall, dass das Telefon weg ist. Zehn Stück, je 10 Zeichen
    // aus dem Base32-Alphabet - das sind 50 Bit und damit nicht zu raten.
    public static IReadOnlyList<string> NewRecoveryCodes(int anzahl = 10)
    {
        var codes = new List<string>(anzahl);
        for (var i = 0; i < anzahl; i++)
        {
            var zeichen = new char[10];
            for (var j = 0; j < zeichen.Length; j++)
            {
                zeichen[j] = Base32Alphabet[RandomNumberGenerator.GetInt32(Base32Alphabet.Length)];
            }

            codes.Add(new string(zeichen, 0, 5) + "-" + new string(zeichen, 5, 5));
        }

        return codes;
    }

    // Gespeichert wird nur der Abdruck, nie der Code selbst. SHA-256 genügt hier, wo ein Passwort
    // es nicht täte: ein Wiederherstellungs-Code ist nichts Ausgedachtes, sondern 50 zufällige Bit -
    // da hilft kein Wörterbuch, und ein langsames Verfahren würde nur das Anmelden bremsen.
    public static string HashRecoveryCode(string code)
    {
        var sauber = Normalise(code);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sauber))).ToLowerInvariant();
    }

    public static string Normalise(string code)
    {
        return new string((code ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private static string Compute(byte[] key, long step)
    {
        var zaehler = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            zaehler[i] = (byte)(step & 0xff);
            step >>= 8;
        }

        var hash = HMACSHA1.HashData(key, zaehler);
        var versatz = hash[^1] & 0x0f;
        var wert = ((hash[versatz] & 0x7f) << 24)
            | ((hash[versatz + 1] & 0xff) << 16)
            | ((hash[versatz + 2] & 0xff) << 8)
            | (hash[versatz + 3] & 0xff);

        return (wert % 1_000_000).ToString("D" + Digits);
    }

    // Zeichenweiser Vergleich mit fester Laufzeit: wer messen kann, wie lange ein Vergleich dauert,
    // kann einen Code sonst Stelle für Stelle erraten.
    private static bool FixedTimeEquals(string a, string b)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a),
            Encoding.ASCII.GetBytes(b));
    }

    private static string ToBase32(byte[] daten)
    {
        var bauer = new StringBuilder((daten.Length * 8 + 4) / 5);
        int puffer = 0, bits = 0;
        foreach (var b in daten)
        {
            puffer = (puffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bauer.Append(Base32Alphabet[(puffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            bauer.Append(Base32Alphabet[(puffer << (5 - bits)) & 31]);
        }

        return bauer.ToString();
    }

    private static byte[] FromBase32(string text)
    {
        var sauber = text.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", "");
        var bytes = new List<byte>(sauber.Length * 5 / 8);
        int puffer = 0, bits = 0;
        foreach (var c in sauber)
        {
            var wert = Base32Alphabet.IndexOf(c);
            if (wert < 0)
            {
                throw new FormatException("Kein Base32-Zeichen: " + c);
            }

            puffer = (puffer << 5) | wert;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((puffer >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
