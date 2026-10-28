using MynAI.Windows.Auth;

var signature = new string('A', 43);
const long expiry = 1790000000L;

var payload = DesktopPairingQrPayload.Build("abcde-23456", expiry, signature);
var expected = $"MYNASSISTANT-PAIR:ABCDE23456:{expiry}:{signature}";
if (!string.Equals(payload, expected, StringComparison.Ordinal))
    throw new InvalidOperationException($"Unexpected Windows QR payload: {payload}");

ExpectArgumentFailure(() => DesktopPairingQrPayload.Build(
    "ABCDE2345O", expiry, signature), "server-forbidden code character");
ExpectArgumentFailure(() => DesktopPairingQrPayload.Build(
    "ABCDE23456", 0, signature), "invalid expiry");
ExpectArgumentFailure(() => DesktopPairingQrPayload.Build(
    "ABCDE23456", expiry, "bad-signature"), "malformed signature");
ExpectArgumentFailure(() => DesktopPairingQrPayload.Build(
    "ABCDE23456", expiry, signature + ":"), "signature delimiter injection");

Console.WriteLine("PASS: Windows QR writer matches Android marker/code/expiry/signature contract and rejects malformed fields.");

static void ExpectArgumentFailure(Action action, string scenario)
{
    try
    {
        action();
    }
    catch (ArgumentException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected invalid QR input to be rejected: {scenario}.");
}
