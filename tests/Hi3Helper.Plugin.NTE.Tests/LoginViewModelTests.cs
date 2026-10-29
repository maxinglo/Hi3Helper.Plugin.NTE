using Nte.Login;
using Nte.LoginHost.Accounts;
using Nte.LoginHost.Services;
using Nte.LoginHost.ViewModels;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class LoginViewModelTests
{
    private sealed class Secret : ITransientLoginSecret
    {
        public string Value = "";
        public int Consumed;

        public string Consume()
        {
            Consumed++;
            string value = Value;
            Clear();
            return value;
        }

        public void Clear() => Value = "";
    }

    private sealed class LoginService : IOverseasLoginService
    {
        public NteOverseasRegion Region { get; set; } = NteOverseasRegion.Global;
        public IReadOnlyList<NtePhoneCountry> PhoneCountries => NtePhoneCountry.Preview;
        public bool Initialized { get; set; } = true;
        public TimeSpan CaptchaCooldown => TimeSpan.Zero;
        public int Calls, AuthCalls;
        public string? Email, Secret;
        public bool Remember;
        public Exception? Error;
        public TaskCompletionSource? Pending;
        public IReadOnlyList<LocalAccount> ListAccounts() => [];

        public Task InitializeAsync(CancellationToken token)
        {
            Calls++;
            Initialized = true;
            return Task.CompletedTask;
        }

        public Task<GlobalAccountSnapshot> LoadAccountsAsync(CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new GlobalAccountSnapshot([], new Dictionary<Guid, AccountDisplayProfile>(), null,
                false));
        }

        public bool RequiresHello(Guid id) => false;
        public Task RestoreAsync(Guid id, CancellationToken token) => Task.CompletedTask;
        public Task RenameAsync(Guid id, string label, CancellationToken token) => Task.CompletedTask;
        public Task RemoveAsync(Guid id, CancellationToken token) => Task.CompletedTask;

        public Task SendCodeAsync(string email, CancellationToken token)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public async Task SignInEmailAsync(string email, string secret, bool password, bool remember,
            CancellationToken token)
        {
            Calls++;
            AuthCalls++;
            Email = email;
            Secret = secret;
            Remember = remember;
            if (Pending != null) await Pending.Task.WaitAsync(token);
            if (Error != null) throw Error;
            token.ThrowIfCancellationRequested();
        }

        public Task SignInBrowserAsync(int provider, bool remember, CancellationToken token)
        {
            Calls++;
            AuthCalls++;
            return Task.CompletedTask;
        }

        public Task SendPhoneCodeAsync(string countryCode, string phone, CancellationToken token)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task SignInPhoneAsync(string countryCode, string phone, string secret, bool password, bool remember,
            CancellationToken token)
        {
            Calls++;
            AuthCalls++;
            Email = countryCode + phone;
            Secret = secret;
            Remember = remember;
            return Task.CompletedTask;
        }
    }

    private static OverseasLoginViewModel Model(LoginService service, Secret secret, bool preview = false) =>
        new(service, secret, key => key, _ => "SafeAuthenticationFailure", preview);

    [Fact]
    public async Task PreviewNavigationAndCommandsNeverAuthenticate()
    {
        LoginService service = new();
        Secret secret = new();
        using var model = Model(service, secret, true);
        Task<bool> lifetime = model.RunAsync(TestContext.Current.CancellationToken);
        model.SetPreviewAccounts(0);
        model.EmailCommand.Execute(null);
        model.Email = "person@example.com";
        model.Agreement = true;
        await model.SendCodeCommand.ExecuteAsync();
        secret.Value = "synthetic password";
        await model.SubmitCommand.ExecuteAsync();
        await model.GoogleCommand.ExecuteAsync();
        Assert.Equal(0, service.Calls);
        Assert.True(model.CodeCooldown > TimeSpan.Zero);
        model.Cancel();
        Assert.False(await lifetime);
    }

    [Fact]
    public async Task AgreementAndEmailValidationRunBeforeReadingThePassword()
    {
        LoginService service = new();
        Secret secret = new();
        using var model = Model(service, secret);
        Task<bool> lifetime = model.RunAsync(TestContext.Current.CancellationToken);
        model.EmailCommand.Execute(null);
        model.Email = "invalid";
        secret.Value = "private input";
        await model.SubmitCommand.ExecuteAsync();
        Assert.Equal("AgreementRequired", model.Status);
        Assert.Equal(0, secret.Consumed);
        model.Agreement = true;
        await model.SubmitCommand.ExecuteAsync();
        Assert.Equal("InvalidEmail", model.Status);
        Assert.Equal(0, secret.Consumed);
        Assert.Equal(0, service.AuthCalls);
        model.Cancel();
        Assert.False(await lifetime);
    }

    [Fact]
    public async Task SubmitConsumesTransientSecretOnceAndCarriesTheRememberChoice()
    {
        LoginService service = new();
        Secret secret = new();
        using var model = Model(service, secret);
        Task<bool> lifetime = model.RunAsync(TestContext.Current.CancellationToken);
        model.EmailCommand.Execute(null);
        model.Email = "person@example.com";
        model.Agreement = true;
        model.Remember = false;
        secret.Value = "private input";
        await model.SubmitCommand.ExecuteAsync();
        Assert.True(await lifetime);
        Assert.Equal(1, secret.Consumed);
        Assert.Equal("", secret.Value);
        Assert.Equal("person@example.com", service.Email);
        Assert.Equal("private input", service.Secret);
        Assert.False(service.Remember);
        Assert.DoesNotContain("private input", model.Status);
    }

    [Fact]
    public async Task BusyAuthenticationRejectsDuplicateCommandsAndCancelDoesNotReportSuccess()
    {
        LoginService service = new() { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Secret secret = new();
        using var model = Model(service, secret);
        Task<bool> lifetime = model.RunAsync(TestContext.Current.CancellationToken);
        model.EmailCommand.Execute(null);
        model.Email = "person@example.com";
        model.Agreement = true;
        secret.Value = "input";
        Task submit = model.SubmitCommand.ExecuteAsync();
        Assert.True(model.IsBusy);
        await model.SubmitCommand.ExecuteAsync();
        await model.GoogleCommand.ExecuteAsync();
        Assert.Equal(1, service.AuthCalls);
        model.Cancel();
        await submit;
        Assert.False(await lifetime);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task AuthenticationFailureCannotLeakExceptionPayloadIntoThePanel()
    {
        LoginService service = new() { Error = new IOException("credential=must-never-be-shown") };
        Secret secret = new();
        using var model = Model(service, secret);
        Task<bool> lifetime = model.RunAsync(TestContext.Current.CancellationToken);
        model.EmailCommand.Execute(null);
        model.Email = "person@example.com";
        model.Agreement = true;
        secret.Value = "input";
        await model.SubmitCommand.ExecuteAsync();
        Assert.Equal("SafeAuthenticationFailure", model.Status);
        Assert.False(model.IsBusy);
        Assert.False(lifetime.IsCompleted);
        model.Cancel();
        await lifetime;
    }

    [Fact]
    public async Task NavigationAndEmailModeChangesClearTransientCredentials()
    {
        LoginService service = new();
        Secret secret = new();
        using var model = Model(service, secret);
        Task<bool> lifetime = model.RunAsync(TestContext.Current.CancellationToken);
        model.EmailCommand.Execute(null);
        model.EmailCode = "123456";
        secret.Value = "input";
        model.UsePassword = false;
        Assert.Equal("", secret.Value);
        Assert.Equal("", model.EmailCode);
        secret.Value = "another input";
        model.EmailCode = "654321";
        model.BackCommand.Execute(null);
        Assert.Equal("", secret.Value);
        Assert.Equal("", model.EmailCode);
        model.Cancel();
        await lifetime;
    }

    [Fact]
    public async Task TaiwanPanelCannotSubmitTheInternationalEmailMethod()
    {
        LoginService service = new() { Region = NteOverseasRegion.Taiwan };
        Secret secret = new();
        using var model = Model(service, secret, true);
        Task<bool> lifetime = model.RunAsync(TestContext.Current.CancellationToken);
        model.SetPreviewAccounts(0);
        Assert.False(model.SupportsEmail);
        Assert.False(model.EmailCommand.CanExecute(null));
        model.EmailCommand.Execute(null);
        Assert.Equal(GlobalLoginPage.Methods, model.Page);
        model.Agreement = true;
        await model.GoogleCommand.ExecuteAsync();
        Assert.Equal(0, service.AuthCalls);
        Assert.Equal(0, secret.Consumed);
        model.Cancel();
        Assert.False(await lifetime);
    }

    [Fact]
    public async Task TaiwanPhoneValidationAndConsentPrecedeSecretConsumptionAndServiceCalls()
    {
        LoginService service = new() { Region = NteOverseasRegion.Taiwan };
        Secret secret = new();
        using var model = Model(service, secret);
        Task<bool> lifetime = model.RunAsync(TestContext.Current.CancellationToken);
        int calls = service.Calls;
        model.PhoneCommand.Execute(null);
        Assert.Equal(GlobalLoginPage.Phone, model.Page);
        Assert.False(model.UsePassword);
        model.Phone = "0910000000";
        model.EmailCode = "123456";
        await model.SendCodeCommand.ExecuteAsync();
        Assert.Equal(calls, service.Calls);
        Assert.Equal("AgreementRequired", model.Status);
        model.Agreement = true;
        model.CountryCode = "+86";
        model.UsePassword = true;
        secret.Value = "synthetic-password";
        await model.SubmitCommand.ExecuteAsync();
        Assert.Equal(0, secret.Consumed);
        Assert.Equal(0, service.AuthCalls);
        Assert.Equal("InvalidOverseasPhone", model.Status);
        model.CountryCode = "+886";
        Assert.Equal("", secret.Value);
        model.UsePassword = false;
        model.EmailCode = "123456";
        await model.SubmitCommand.ExecuteAsync();
        Assert.True(await lifetime);
        Assert.Equal("+8860910000000", service.Email);
        Assert.Equal("123456", service.Secret);
    }

    [Fact]
    public async Task TaiwanPhonePreviewNeverSendsSmsAndCountryChangesClearTheCode()
    {
        LoginService service = new() { Region = NteOverseasRegion.Taiwan };
        Secret secret = new();
        using var model = Model(service, secret, true);
        Task<bool> lifetime = model.RunAsync(TestContext.Current.CancellationToken);
        model.SetPreviewAccounts(0);
        model.PhoneCommand.Execute(null);
        model.Phone = "0910000000";
        model.Agreement = true;
        await model.SendCodeCommand.ExecuteAsync();
        Assert.True(model.CodeCooldown > TimeSpan.Zero);
        model.EmailCode = "123456";
        model.CountryCode = "+852";
        Assert.Equal("", model.EmailCode);
        Assert.False(model.SendCodeCommand.CanExecute(null));
        Assert.Equal(0, service.Calls);
        model.Cancel();
        Assert.False(await lifetime);
    }

    private sealed class SwitchClient : INteRegionSwitchClient
    {
        public int Applied;
        public bool RejectPreview;

        public Task<NteSwitchSummary> PlanAsync(NteRegionSwitchOptions options, IProgress<NteSwitchProgress> progress,
            CancellationToken token) =>
            RejectPreview
                ? Task.FromException<NteSwitchSummary>(new IOException("fixture preview failed"))
                : Task.FromResult(new NteSwitchSummary("3000001", "2000013", "1.4.6", 100, 9, 1, 100, false, false));

        public Task<NteSwitchSummary> SwitchAsync(NteRegionSwitchOptions options, IProgress<NteSwitchProgress> progress,
            CancellationToken token)
        {
            Applied++;
            return Task.FromResult(new NteSwitchSummary("3000001", "2000013", "1.4.6", 100, 9, 1, 100, true, false));
        }
    }

    [Fact]
    public async Task SwitchViewModelRequiresAPreviewAndCompletesThroughTheService()
    {
        SwitchClient client = new();
        using var model = new RegionSwitchViewModel(client, new(Path.GetTempPath(), NteHostRegion.Taiwan),
            key => key == "ServerSwitchSummary" ? "{0}/{1}/{2}/{3}/{4}" : key);
        Assert.False(model.SwitchCommand.CanExecute(null));
        await model.SwitchCommand.ExecuteAsync();
        Assert.Equal(0, client.Applied);
        await model.CheckCommand.ExecuteAsync();
        Assert.True(model.SwitchCommand.CanExecute(null));
        await model.SwitchCommand.ExecuteAsync();
        Assert.Equal(1, client.Applied);
        Assert.True(model.IsCompleted);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task FailedRecheckInvalidatesThePreviousSwitchPreview()
    {
        SwitchClient client = new();
        using var model = new RegionSwitchViewModel(client, new(Path.GetTempPath(), NteHostRegion.Taiwan),
            key => key == "ServerSwitchSummary" ? "{0}/{1}/{2}/{3}/{4}" : key);
        await model.CheckCommand.ExecuteAsync();
        Assert.True(model.SwitchCommand.CanExecute(null));
        client.RejectPreview = true;
        await model.CheckCommand.ExecuteAsync();
        Assert.False(model.SwitchCommand.CanExecute(null));
        Assert.Equal("", model.Summary);
        await model.SwitchCommand.ExecuteAsync();
        Assert.Equal(0, client.Applied);
    }
}
