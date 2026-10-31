using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Google.Apis.Auth.OAuth2;
using Google.Apis.PeopleService.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace MynAI.Windows.Google;

public sealed class GoogleOAuthService
{
    // This page is shown by the local loopback callback before token exchange finishes.
    // It intentionally reports receipt of the OAuth response rather than claiming that
    // the final API authorization succeeded. The app displays the final result separately.
    private const string OAuthCallbackClosePage =
        "<!doctype html><html lang='tr'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'><title>MynAssistant</title></head>" +
        "<body style='margin:0;background:#101827;color:#f4f7ff;font-family:Segoe UI,Arial,sans-serif;display:grid;min-height:100vh;place-items:center'>" +
        "<main style='max-width:520px;padding:36px;text-align:center'><h2>Google yanıtı alındı</h2>" +
        "<p>Bu pencereyi kapatabilirsiniz. MynAssistant bağlantı sonucunu kontrol ediyor.</p></main></body></html>";

    private static readonly string[] Scopes =
    [
        "openid",
        "https://www.googleapis.com/auth/userinfo.email",
        "https://www.googleapis.com/auth/userinfo.profile",
        "https://www.googleapis.com/auth/contacts.readonly"
    ];

    private readonly string _tokenDirectory;

    private UserCredential? _credential;
    private PeopleServiceService? _peopleService;

    public GoogleOAuthService()
    {
        var currentTokens = Path.Combine(GoogleOAuthConfigStore.CurrentDirectory, "Tokens");
        var legacyTokens = Path.Combine(GoogleOAuthConfigStore.LegacyDirectory, "Tokens");

        // Keep prior OAuth token storage if it already exists, so upgrading
        // from the MynAI folder does not silently disconnect the user's account.
        _tokenDirectory = !Directory.Exists(currentTokens) && Directory.Exists(legacyTokens)
            ? legacyTokens
            : currentTokens;
    }

    public async Task<GoogleAccount> SignInAsync(
        CancellationToken cancellationToken = default)
    {
        var clientSecretPath = GoogleOAuthConfigStore.GetPath();
        if (!File.Exists(clientSecretPath))
        {
            throw new FileNotFoundException(
                "Google Kişiler masaüstü yetkilendirmesi bu kurulumda yapılandırılmamış. Telefon-PC bağlantısı için Hesap bölümündeki Firebase girişini kullanın.",
                clientSecretPath);
        }

        Directory.CreateDirectory(_tokenDirectory);

        using var stream = new FileStream(
            clientSecretPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        _credential =
            await GoogleWebAuthorizationBroker.AuthorizeAsync(
                GoogleClientSecrets.FromStream(stream).Secrets,
                Scopes,
                "mynai-user",
                cancellationToken,
                new FileDataStore(
                    _tokenDirectory,
                    true),
                new LocalServerCodeReceiver(OAuthCallbackClosePage));

        _peopleService =
            new PeopleServiceService(
                new BaseClientService.Initializer
                {
                    HttpClientInitializer = _credential,
                    ApplicationName = "MynAI"
                });

        var request =
            _peopleService.People.Get("people/me");

        request.PersonFields =
            "names,emailAddresses,photos";

        var person =
            await request.ExecuteAsync(
                cancellationToken);

        return new GoogleAccount
        {
            Id = person.ResourceName ?? string.Empty,

            Name =
                person.Names?
                    .FirstOrDefault()?
                    .DisplayName
                ?? "Google Kullanıcısı",

            Email =
                person.EmailAddresses?
                    .FirstOrDefault()?
                    .Value
                ?? string.Empty,

            PhotoUrl =
                person.Photos?
                    .FirstOrDefault()?
                    .Url
        };
    }

    public async Task<IReadOnlyList<GoogleContact>>
        GetContactsAsync(
            CancellationToken cancellationToken = default)
    {
        if (_peopleService is null)
        {
            throw new InvalidOperationException(
                "Önce Google hesabına giriş yapılmalı.");
        }

        var contacts = new List<GoogleContact>();

        string? pageToken = null;

        do
        {
            var request =
                _peopleService.People.Connections.List(
                    "people/me");

            request.PersonFields =
                "names,emailAddresses,phoneNumbers";

            request.PageSize = 500;
            request.PageToken = pageToken;

            var response =
                await request.ExecuteAsync(
                    cancellationToken);

            foreach (var person in
                     response.Connections ?? [])
            {
                var name =
                    person.Names?
                        .FirstOrDefault()?
                        .DisplayName
                    ?? string.Empty;

                var email =
                    person.EmailAddresses?
                        .FirstOrDefault()?
                        .Value
                    ?? string.Empty;

                var phone =
                    person.PhoneNumbers?
                        .FirstOrDefault()?
                        .Value
                    ?? string.Empty;

                if (string.IsNullOrWhiteSpace(name) &&
                    string.IsNullOrWhiteSpace(email) &&
                    string.IsNullOrWhiteSpace(phone))
                {
                    continue;
                }

                contacts.Add(
                    new GoogleContact
                    {
                        Name = name,
                        Email = email,
                        Phone = phone
                    });
            }

            pageToken = response.NextPageToken;

        } while (!string.IsNullOrWhiteSpace(pageToken));

        return contacts.AsReadOnly();
    }
}

