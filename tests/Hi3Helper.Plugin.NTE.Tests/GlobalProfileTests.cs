using System.Text;
using System.Text.Json;
using Nte.Login;
using Nte.LoginHost;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class GlobalProfileTests
{
    private static Dictionary<string, string> Google(string issuer = "https://accounts.google.com", string audience = "123-fixture.apps.googleusercontent.com",
        string subject = "fixture-google-subject", bool verified = true, bool expired = false)
    {
        static string Encode(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Deliberately not a signed token. This tests optional display parsing,
        // never cryptographic verification or the official PC exchange.
        return new() { ["thirdId"] = "fixture-google-subject", ["thirdUsername"] = "Callback name",
            ["thirdAuthToken"] = Encode(new { alg = "RS256", kid = "fixture-key" }) + "." + Encode(new
            { iss = issuer, aud = audience, sub = subject, email = "person@example.com", name = "Claim name", email_verified = verified, iat = now - 30, exp = expired ? now - 120 : now + 3600 }) + ".fixture-signature" };
    }
    [Fact]
    public void DisplayHintsRequireProjectSubjectIssuerAndLifetimeAssociation()
    {
        NteGlobalProfile profile = NteGlobalProfile.AfterBrowserExchange(2, Google(), "123");
        Assert.Equal("person@example.com", profile.Email); Assert.Equal("Claim name", profile.Name);
        Assert.Null(NteGlobalProfile.AfterBrowserExchange(2, Google(), null).Email);
        Assert.Null(NteGlobalProfile.AfterBrowserExchange(2, Google(subject: "other-subject"), "123").Email);
        Assert.Null(NteGlobalProfile.AfterBrowserExchange(2, Google(issuer: "https://untrusted.example"), "123").Email);
        Assert.Null(NteGlobalProfile.AfterBrowserExchange(2, Google(audience: "999-other.apps.googleusercontent.com"), "123").Email);
        Assert.Null(NteGlobalProfile.AfterBrowserExchange(2, Google(verified: false), "123").Email);
        Assert.Null(NteGlobalProfile.AfterBrowserExchange(2, Google(expired: true), "123").Email);
    }
    [Fact]
    public void MalformedOrMissingOptionalProfileDoesNotInventAnEmail()
    {
        Assert.Null(NteGlobalProfile.AfterBrowserExchange(2, new Dictionary<string, string>(), "123").Email);
        Assert.Null(NteGlobalProfile.AfterBrowserExchange(2, new Dictionary<string, string> { ["thirdId"] = "x", ["thirdAuthToken"] = "bad.jwt.value" }, "123").Email);
        using JsonDocument result = JsonDocument.Parse("{\"username\":\"looks.like.email@example.com\"}");
        Assert.Null(NteGlobalProfile.FromService(result.RootElement).Email);
        Assert.Null(NteGlobalProfile.FromEmail("Person <person@example.com>").Email);
        Assert.Null(NteGlobalProfile.TextValue("spoof\u202Ename", 80));
    }
    [Fact]
    public void FacebookCallbackDetailsAndRemarksAreDisplayDataWithoutProviderTokenPersistence()
    {
        NteGlobalProfile profile = NteGlobalProfile.AfterBrowserExchange(1, new Dictionary<string, string>
        { ["thirdEmail"] = "person@example.com", ["thirdUsername"] = "Fixture name", ["thirdAuthToken"] = "provider-secret-fixture" }, null);
        using JsonDocument response = JsonDocument.Parse("{\"code\":0,\"result\":{\"uid\":\"fixture-pc-user\",\"token\":\"fixture-pc-token\"}}");
        using NteGlobalIdentity identity = NteGlobalIdentity.FromService(response.RootElement, 1).WithProfile(profile);
        using NteGlobalIdentity clone = identity.Clone(); Assert.Equal(profile, clone.Profile);
        string stored = Encoding.UTF8.GetString(StoredGlobalIdentity.Encode(identity));
        Assert.DoesNotContain("person@example.com", stored); Assert.DoesNotContain("provider-secret-fixture", stored);
        Assert.Contains("fixture-pc-token", stored); Assert.DoesNotContain("person@example.com", profile.ToString());
    }
}
