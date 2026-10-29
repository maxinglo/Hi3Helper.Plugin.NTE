using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nte.Login;
using Nte.LoginHost;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class TaiwanPhoneAuthenticationTests
{
    private static NteOverseasApplicationConfiguration Configuration(NteOverseasRegion? region = null)
    {
        region ??= NteOverseasRegion.Taiwan;
        return new(Encoding.ASCII.GetBytes("fixture-application-material-40-characters"), region.PackageName, region.Service, region.Area, region: region);
    }
    private const string Countries = """{"code":0,"result":{"codes":[{"countryCode":"+886","areaName":"臺灣"},{"countryCode":"+852","areaName":"香港"},{"countryCode":"+853","areaName":"澳門"}]}}""";
    private static Dictionary<string,string> Form(string body) => body.Split('&').Select(p => p.Split('=',2)).ToDictionary(p=>Uri.UnescapeDataString(p[0]),p=>Uri.UnescapeDataString(p[1].Replace('+',' ')));

    [Fact]
    public async Task PublicCountriesUseTaiwanDefaultAndSmsRequestsAreEncryptedExplicitAndRateLimited()
    {
        using var config = Configuration();
        using var handler = new Handler(async request =>
        {
            Assert.Equal("mapitw.playcomb.com", request.RequestUri!.Host);
            string path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("getV2")) return """{"code":0,"result":{"loginTypes":[7,2,1,11],"defaultCountryCode":"+886"}}""";
            if (path.EndsWith("countryCode/list")) return Countries;
            Assert.Equal("/c/api/phone/get/sms/captcha", path);
            string body = await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var fields = Form(body);
            Assert.Equal("+886", fields["countryCode"]);
            Assert.Equal("0910000000", config.DecodeServiceValue(fields["phoneNumber"]));
            Assert.DoesNotContain("0910000000", body); Assert.Equal("2000013", fields["appId"]);
            Assert.Equal(NteGlobalAuthentication.Sign(fields, config.ApplicationKey), fields["sign"]);
            return """{"code":0,"result":{}}""";
        });
        using HttpClient http = new(handler); using NteGlobalAuthentication api = new(config,"1.4.6",http);
        await api.InitializeAsync(TestContext.Current.CancellationToken); await api.LoadPhoneCountriesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[]{"+886","+852","+853"}, api.PhoneCountries.Select(x=>x.CountryCode)); Assert.Equal("+886",api.DefaultCountryCode); Assert.Equal(2,handler.Calls);
        await Assert.ThrowsAsync<NteGlobalAuthenticationException>(()=>api.SendPhoneCaptchaAsync("+86","0910000000",TestContext.Current.CancellationToken)); Assert.Equal(2,handler.Calls);
        await api.SendPhoneCaptchaAsync("+886","0910000000",TestContext.Current.CancellationToken);
        Assert.InRange(api.CaptchaCooldown.TotalSeconds,58,60);
        await Assert.ThrowsAsync<NteGlobalAuthenticationException>(()=>api.SendPhoneCaptchaAsync("+886","0910000000",TestContext.Current.CancellationToken)); Assert.Equal(3,handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhoneLoginUsesThePcEndpointAndRetainsOnlyMaskedDisplayData(bool password)
    {
        using var config = Configuration(); using RSA rsa = RSA.Create(2048);
        using var handler = new Handler(async request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("getV2")) return JsonSerializer.Serialize(new {code=0,result=new{loginTypes=new[]{7,2,1,11},clientPublicKey=Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())}});
            if (path.EndsWith("countryCode/list")) return Countries;
            Assert.Equal(password ? "/c/api/phone/login/password" : "/c/api/phone/login",path);
            var fields=Form(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("+852",fields["countryCode"]); Assert.Equal("50000000",config.DecodeServiceValue(fields["phoneNumber"]));
            if(password) Assert.Equal("synthetic-password",Encoding.UTF8.GetString(rsa.Decrypt(Convert.FromBase64String(fields["password"]),RSAEncryptionPadding.Pkcs1)));
            else Assert.Equal("123456",fields["smsCode"]);
            return """{"code":0,"result":{"uid":"fixture-phone-user","token":"synthetic-token","loginType":7}}""";
        });
        using HttpClient http=new(handler); using NteGlobalAuthentication api=new(config,"1.4.6",http);
        await api.InitializeAsync(TestContext.Current.CancellationToken); await api.LoadPhoneCountriesAsync(TestContext.Current.CancellationToken);
        using var identity=await api.LoginPhoneAsync("+852","50000000",password?"synthetic-password":"123456",password,TestContext.Current.CancellationToken);
        Assert.Equal(7,identity.Provider); Assert.Same(NteOverseasRegion.Taiwan,identity.Region); Assert.Equal("+852 *** 0000",identity.Profile.MaskedPhone);
        byte[] saved=StoredGlobalIdentity.Encode(identity);
        try { Assert.DoesNotContain("50000000",Encoding.UTF8.GetString(saved)); using var restored=StoredGlobalIdentity.Decode(saved,NteOverseasRegion.Taiwan); Assert.Equal(7,restored.Provider); Assert.Throws<InvalidDataException>(()=>StoredGlobalIdentity.Decode(saved,NteOverseasRegion.Global)); }
        finally { CryptographicOperations.ZeroMemory(saved); }
    }

    [Fact]
    public async Task InternationalPhoneRequestsAreRejectedBeforeAnyNetworkRequest()
    {
        using var config=Configuration(NteOverseasRegion.Global); using var handler=new Handler(_=>throw new InvalidOperationException("Must not send")); using HttpClient http=new(handler); using NteGlobalAuthentication api=new(config,"1.4.10",http);
        await Assert.ThrowsAsync<NteGlobalAuthenticationException>(()=>api.LoadPhoneCountriesAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NteGlobalAuthenticationException>(()=>api.LoginPhoneAsync("+886","0910000000","123456",false,TestContext.Current.CancellationToken)); Assert.Equal(0,handler.Calls);
    }

    private sealed class Handler(Func<HttpRequestMessage,Task<string>> answer):HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        { Calls++; return new(HttpStatusCode.OK){Content=new StringContent(await answer(request),Encoding.UTF8,"application/json")}; }
    }
}
