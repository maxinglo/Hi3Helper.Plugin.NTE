using System.Net.Mail;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Nte.Login;
using Nte.LoginHost.Accounts;
using Nte.LoginHost.Localization;

namespace Nte.LoginHost;

internal sealed partial class GlobalLoginView : StackPanel, IDisposable
{
    private enum Page
    {
        Saved,
        Methods,
        Email,
        Help
    }

    private readonly NteGlobalAuthentication api;
    private readonly GlobalAccountStore? accounts;
    private readonly SavedAccountPicker picker;
    private readonly Dictionary<Guid, AccountDisplayProfile> profiles = [];
    private readonly nint owner;
    private readonly bool preview;
    private readonly Func<bool, Func<Task>, Task> protectOperation;
    private readonly Func<NteGlobalIdentity, CancellationToken, Task> deliver;
    private readonly string? initialNotice;
    private Page page, previousPage;

    private readonly StackPanel savedBody = new(),
        newBody = new(),
        methodsBody = new(),
        emailBody = new(),
        helpBody = new();

    private readonly CheckBox remember = new() { IsChecked = true };
    private readonly CheckBox agreement = new();
    private readonly TextBox email = new() { Height = 42, MaxLength = 128 };
    private readonly PasswordBox secret = new() { Height = 42, MaxLength = 256 };
    private readonly TextBox emailCode = new() { Height = 42, MaxLength = 16 };
    private readonly RadioButton passwordMode = new() { GroupName = "EmailMethod", IsChecked = true };
    private readonly RadioButton codeMode = new() { GroupName = "EmailMethod" };
    private readonly Button google = new(), facebook = new(), emailEntry = new(), signIn = new(), captcha = new();
    private readonly Button otherAccount = new(), back = new(), help = new(), retry = new(), cancelWork = new();
    private readonly TextBlock status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel passwordGroup = new(), codeGroup = new();
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Windows.Threading.DispatcherTimer cooldown = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? lifetime, activeOperation;
    private Task work = Task.CompletedTask;
    private bool busy;
    private DateTimeOffset previewNextCode;
    internal event Action? LayoutChanged;

    internal GlobalLoginView(NteGlobalAuthentication api, Func<NteGlobalIdentity, CancellationToken, Task> deliver,
        Func<string, Style> style, GlobalAccountStore? accounts, nint owner, bool preview = false,
        string? notice = null, Func<bool, Func<Task>, Task>? protectOperation = null)
    {
        this.api = api;
        this.deliver = deliver;
        this.accounts = accounts;
        this.owner = owner;
        this.preview = preview;
        this.protectOperation = protectOperation ?? ((mayPrompt, action) => mayPrompt
            ? Task.FromException(new InvalidOperationException("A Hello presentation owner is required."))
            : action());
        initialNotice = notice;
        picker = new(style, Present);
        if (preview) SetPreviewAccounts(3);
        else picker.Reload(accounts?.List() ?? []);
        BuildLayout(style);
        picker.SelectionChanged += UpdateEnabled;
        picker.LayoutChanged += RefreshLayout;
        picker.RenameRequested += RenameAsync;
        picker.RemoveRequested += RemoveAsync;
        otherAccount.Click += (_, _) => SetPage(Page.Methods);
        emailEntry.Click += (_, _) => SetPage(Page.Email);
        back.Click += (_, _) =>
            SetPage(page == Page.Email ? Page.Methods : page == Page.Help ? previousPage : Page.Saved);
        help.Click += (_, _) => ShowHelp();
        cancelWork.Click += (_, _) => activeOperation?.Cancel();
        retry.Click += async (_, _) => await ExecuteAsync(InitializeAsync);
        passwordMode.Checked += (_, _) => ChangeEmailMode();
        codeMode.Checked += (_, _) => ChangeEmailMode();
        google.Click += async (_, _) =>
        {
            if (RequireAgreement()) await OpenBrowserAsync(2);
        };
        facebook.Click += async (_, _) =>
        {
            if (RequireAgreement()) await OpenBrowserAsync(1);
        };
        signIn.Click += async (_, _) => await SubmitAsync();
        captcha.Click += async (_, _) => await SendCodeAsync();
        email.TextChanged += (_, _) =>
        {
            emailCode.Clear();
            UpdateEnabled();
        };
        secret.PasswordChanged += (_, _) => UpdateEnabled();
        emailCode.TextChanged += (_, _) => UpdateEnabled();
        cooldown.Tick += (_, _) =>
        {
            UpdateEnabled();
            if (CodeCooldown == TimeSpan.Zero) cooldown.Stop();
        };
        PreviewKeyDown += async (_, args) =>
        {
            if (args.Key == Key.Escape && busy)
            {
                activeOperation?.Cancel();
                args.Handled = true;
            }
            else if (args.Key == Key.Escape && picker.IsManaging)
            {
                picker.EndManage();
                args.Handled = true;
            }
            else if (args.Key == Key.Escape && page is Page.Email or Page.Help)
            {
                SetPage(page == Page.Email ? Page.Methods : previousPage);
                args.Handled = true;
            }
            else if (args.Key == Key.Enter && !picker.IsManaging && page is Page.Saved or Page.Email &&
                     signIn.IsEnabled)
            {
                args.Handled = true;
                await SubmitAsync();
            }
        };
        ChangeEmailMode();
        SetPage(picker.HasAccounts ? Page.Saved : Page.Methods);
    }

    internal async Task<bool> RunAsync(CancellationToken token)
    {
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using CancellationTokenRegistration registration = token.Register(() => Dispatcher.BeginInvoke(Cancel));
        try
        {
            if (preview)
            {
                SetStatus(initialNotice ?? "");
                UpdateEnabled();
            }
            else await ExecuteAsync(InitializeAsync);

            return await completion.Task;
        }
        finally
        {
            lifetime.Cancel();
            activeOperation?.Cancel();
            await work;
        }
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        SetStatus(LoginText.Get("Connecting"));
        if (!api.Initialized) await api.InitializeAsync(token);
        SetStatus(initialNotice ?? "");
        await ReloadAccountsAsync(token);
    }

    private Task RenameAsync()
    {
        if (picker.Selected is not { } selected) return Task.CompletedTask;
        if (picker.Remark.Length is < 1 or > 32 || picker.Remark.Any(char.IsControl))
        {
            SetStatus(LoginText.Get("InvalidLabel"));
            return Task.CompletedTask;
        }

        if (preview)
        {
            picker.RenamePreview(picker.Remark);
            RefreshLayout();
            return Task.CompletedTask;
        }

        string value = picker.Remark;
        return ExecuteAsync(async token =>
        {
            await protectOperation(accounts!.RequiresHello(selected.Id),
                async () => { await accounts.RenameAsync(selected.Id, value, owner, token); });
            await ReloadAccountsAsync(token, selected.Id);
            SetStatus(LoginText.Get("LabelUpdated"));
        });
    }

    private Task RemoveAsync()
    {
        if (picker.Selected is not { } selected) return Task.CompletedTask;
        if (preview)
        {
            picker.RemovePreview();
            if (!picker.HasAccounts) SetPage(Page.Methods);
            RefreshLayout();
            return Task.CompletedTask;
        }

        return ExecuteAsync(async token =>
        {
            await accounts!.RemoveAsync(selected.Id, token);
            await ReloadAccountsAsync(token);
            if (!picker.HasAccounts) page = Page.Methods;
            SetStatus(LoginText.Get("AccountRemoved"));
        });
    }

    private async Task ReloadAccountsAsync(CancellationToken token, Guid? selected = null)
    {
        if (accounts == null) return;
        IReadOnlyList<LocalAccount> entries = accounts.List();
        bool unavailable = false;
        foreach (LocalAccount account in entries)
        {
            try
            {
                if (await accounts.DisplayAsync(account, token) is { } profile) profiles[account.Id] = profile;
            }
            catch (Exception error) when (AccountDisplayStore.IsDisplayError(error))
            {
                unavailable = true;
            }
        }

        picker.Reload(entries, selected ?? picker.Selected?.Id ?? (await accounts.LastAsync(token))?.Id);
        if (unavailable) SetStatus(LoginText.Get("DisplayInfoUnavailable"));
    }

    private async Task SubmitAsync()
    {
        if (busy) return;
        if (!preview && !api.Initialized)
        {
            await ExecuteAsync(InitializeAsync);
            return;
        }

        if (page == Page.Saved)
        {
            if (picker.Selected is not { } selected) return;
            await ExecuteAsync(async token =>
            {
                bool useHello = GlobalAccountStore.ReadProtectionPreference();
                SetStatus(LoginText.Get(useHello || accounts!.RequiresHello(selected.Id)
                    ? "UnlockAccountWithHello"
                    : "ReadingSavedAccount"));
                await protectOperation(useHello || accounts!.RequiresHello(selected.Id), () =>
                    accounts!.RestoreAndDeliverAsync(selected.Id, api, true, owner, deliver, token, useHello));
                completion.TrySetResult(true);
            });
            return;
        }

        if (page != Page.Email || !RequireAgreement() || !ValidateEmail()) return;
        string value = passwordMode.IsChecked == true ? secret.Password : emailCode.Text.Trim();
        if (value.Length == 0)
        {
            SetStatus(LoginText.Get(passwordMode.IsChecked == true ? "EnterPassword" : "EnterEmailCode"));
            if (passwordMode.IsChecked == true) secret.Focus();
            else emailCode.Focus();
            return;
        }

        string address = email.Text.Trim();
        bool usePassword = passwordMode.IsChecked == true;
        await ExecuteAsync(async token =>
        {
            using NteGlobalIdentity identity = await api.LoginEmailAsync(address, value, usePassword, token);
            secret.Clear();
            emailCode.Clear();
            await DeliverAsync(identity, token);
        });
    }

    private bool RequireAgreement()
    {
        if (agreement.IsChecked == true) return true;
        SetStatus(LoginText.Get("AgreementRequired"));
        agreement.Focus();
        return false;
    }

    private bool ValidateEmail()
    {
        string value = email.Text.Trim();
        if (value.Length <= 128 && MailAddress.TryCreate(value, out MailAddress? address) &&
            address.Address == value) return true;
        SetStatus(LoginText.Get("InvalidEmail"));
        email.Focus();
        return false;
    }

    private TimeSpan CodeCooldown => preview
        ? (previewNextCode > DateTimeOffset.UtcNow ? previewNextCode - DateTimeOffset.UtcNow : TimeSpan.Zero)
        : api.CaptchaCooldown;

    private async Task SendCodeAsync()
    {
        if (!RequireAgreement() || !ValidateEmail()) return;
        if (preview)
        {
            previewNextCode = DateTimeOffset.UtcNow.AddSeconds(60);
            cooldown.Start();
            SetStatus(LoginText.Get("PreviewNoAuthentication"));
            UpdateEnabled();
            return;
        }

        string address = email.Text.Trim();
        await ExecuteAsync(async token =>
        {
            await api.SendEmailCaptchaAsync(address, token);
            SetStatus(LoginText.Get("EmailCodeSent"));
            cooldown.Start();
        });
    }

    private void ChangeEmailMode()
    {
        secret.Clear();
        emailCode.Clear();
        passwordGroup.Visibility = passwordMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        codeGroup.Visibility = passwordMode.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        UpdateEnabled();
        RefreshLayout();
    }

    private void UpdateEnabled()
    {
        bool ready = preview || api.Initialized;
        email.IsEnabled = secret.IsEnabled = emailCode.IsEnabled = passwordMode.IsEnabled = codeMode.IsEnabled = !busy;
        agreement.IsEnabled = remember.IsEnabled = picker.IsEnabled = !busy;
        google.IsEnabled = facebook.IsEnabled = emailEntry.IsEnabled = !busy && ready;
        otherAccount.IsEnabled = back.IsEnabled = help.IsEnabled = retry.IsEnabled = !busy;
        captcha.IsEnabled = !busy && ready && CodeCooldown == TimeSpan.Zero;
        captcha.Content = LoginText.Get(CodeCooldown > TimeSpan.Zero ? "SmsResendCountdown" : "SendEmailCode",
            (int)Math.Ceiling(CodeCooldown.TotalSeconds));
        signIn.Content =
            LoginText.Get(!ready ? "RetryConnection" : page == Page.Saved ? "ContinueSignIn" : "EmailSignIn");
        signIn.IsEnabled = !busy && (page == Page.Email || page == Page.Saved && (picker.Selected != null || !ready));
        signIn.IsDefault = signIn.IsEnabled && !picker.IsManaging;
    }

    private Task OpenBrowserAsync(int provider) => ExecuteAsync(async token =>
    {
        using GlobalBrowserLogin browser = new();
        SetStatus(LoginText.Get("BrowserPending"));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using JsonDocument response =
            await browser.WaitAsync(api.CreateBrowserLoginUri(provider, browser.Target), timeout.Token);
        using NteGlobalIdentity identity =
            await api.CompleteBrowserLoginAsync(response.RootElement, provider, timeout.Token);
        await DeliverAsync(identity, timeout.Token);
    });

    private async Task DeliverAsync(NteGlobalIdentity identity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SetStatus(LoginText.Get("PreparingSavedAccount"));
        if (accounts == null) await deliver(identity, token);
        else
        {
            bool useHello = GlobalAccountStore.ReadProtectionPreference();
            string label = identity.Profile.Name ?? identity.Profile.Email ?? LoginText.Get("SavedAccountNumber",
                NteGlobalIdentity.ReadUserId(identity.Result.GetProperty("uid"))[
                    ^Math.Min(6, NteGlobalIdentity.ReadUserId(identity.Result.GetProperty("uid")).Length)..]);
            if (label.Length > 32) label = label[..(char.IsHighSurrogate(label[31]) ? 31 : 32)];
            await protectOperation(accounts.SaveMayPrompt(useHello), () =>
                accounts.SaveAndDeliverAsync(identity, label, remember.IsChecked == true, owner, deliver, token,
                    useHello));
        }

        completion.TrySetResult(true);
    }

    private Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        if (busy || lifetime == null || completion.Task.IsCompleted) return Task.CompletedTask;
        if (preview)
        {
            SetStatus(LoginText.Get("PreviewNoAuthentication"));
            return Task.CompletedTask;
        }

        busy = true;
        activeOperation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        RefreshLayout();
        return work = RunActionAsync();

        async Task RunActionAsync()
        {
            try
            {
                await action(activeOperation.Token);
            }
            catch (OperationCanceledException)
            {
                if (!completion.Task.IsCompleted) SetStatus(LoginText.Get("AuthorizationCancelled"));
            }
            catch (NteGlobalAuthenticationException error)
            {
                SetStatus(LoginText.Get(error.Key, error.Arguments));
            }
            catch (HelloProtectionException error)
            {
                SetStatus(LoginText.Get("HelloProtectionFailed", $"0x{error.NativeError:X8}"));
            }
            catch (Exception error) when (error is HttpRequestException or IOException
                                              or System.Security.Cryptography.CryptographicException
                                              or InvalidOperationException or JsonException
                                              or UnauthorizedAccessException or KeyNotFoundException
                                              or AggregateException)
            {
                SetStatus(LoginText.Get("GlobalSignInFailed"));
            }
            finally
            {
                activeOperation.Dispose();
                activeOperation = null;
                busy = false;
                RefreshLayout();
            }
        }
    }

    internal void ShowHelp()
    {
        if (busy) return;
        if (page == Page.Help)
        {
            SetPage(previousPage);
            return;
        }

        previousPage = page;
        SetPage(Page.Help);
    }

    private void SetStatus(string text)
    {
        status.Text = text;
        status.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        LayoutChanged?.Invoke();
    }

    internal void ShowNotice(string text) => SetStatus(text);

    internal void Cancel()
    {
        lifetime?.Cancel();
        activeOperation?.Cancel();
        secret.Clear();
        emailCode.Clear();
        completion.TrySetResult(false);
    }

    public void Dispose()
    {
        Cancel();
        cooldown.Stop();
        lifetime?.Dispose();
    }
}
