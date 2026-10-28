namespace MynAI.Windows.Auth;

/// <summary>
/// Builds the short-lived QR payload shared with the Android scanner.
/// This validates shape only; the server remains authoritative for HMAC and expiry.
/// </summary>
public static class DesktopPairingQrPayload
{
    private const string Marker = "MYNASSISTANT-PAIR";
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Build(string pairingCode, long expiresAtUnix, string signature)
    {
        var normalizedCode = new string((pairingCode ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToUpperInvariant();

        if (normalizedCode.Length != 10 ||
            normalizedCode.Any(character => CodeAlphabet.IndexOf(character) < 0))
        {
            throw new ArgumentException(
                "The pairing code must contain exactly 10 characters from the server's code alphabet.",
                nameof(pairingCode));
        }

        if (expiresAtUnix <= 0)
            throw new ArgumentOutOfRangeException(nameof(expiresAtUnix), "QR expiry must be a positive Unix timestamp.");

        if (string.IsNullOrWhiteSpace(signature) ||
            signature.Length != 43 ||
            signature.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            throw new ArgumentException(
                "The server QR signature must be a 43-character base64url HMAC.",
                nameof(signature));
        }

        return $"{Marker}:{normalizedCode}:{expiresAtUnix}:{signature}";
    }
}
