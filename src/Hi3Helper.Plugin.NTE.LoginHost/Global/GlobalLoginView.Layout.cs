using Nte.LoginHost.ViewModels;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nte.LoginHost.Accounts;
using Nte.LoginHost.Localization;

namespace Nte.LoginHost;

internal sealed partial class OverseasLoginView
{
    private void BuildLayout(Func<string, Style> style)
    {
        Configure(back, "BackToSavedAccounts", "back");
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.Margin = new(0, 0, 0, 10);
        Children.Add(back);
        savedBody.Children.Add(picker);
        Children.Add(savedBody);
        Configure(google, "SignInGoogle", "provider-google", "ProviderButtonStyle");
        Configure(facebook, "SignInFacebook", "provider-facebook", "ProviderButtonStyle");
        google.Content = ProviderLabel("google-g.png", "SignInGoogle");
        facebook.Content = ProviderLabel("facebook-primary.png", "SignInFacebook");
        Configure(emailEntry, "SignInEmail", "email-login", "SmsButtonStyle");
        emailEntry.FontSize = 14;
        google.Margin = facebook.Margin = new(0, 0, 0, 10);
        methodsBody.Children.Add(google);
        methodsBody.Children.Add(facebook);
        methodsBody.Children.Add(emailEntry);
        newBody.Children.Add(methodsBody);
        email.Style = emailCode.Style = style("LoginTextBoxStyle");
        secret.Style = style("LoginPasswordBoxStyle");
        AutomationProperties.SetName(email, LoginText.Get("Email"));
        AutomationProperties.SetAutomationId(email, "email");
        AutomationProperties.SetName(secret, LoginText.Get("Password"));
        AutomationProperties.SetAutomationId(secret, "secret");
        AutomationProperties.SetName(emailCode, LoginText.Get("EmailCode"));
        AutomationProperties.SetAutomationId(emailCode, "email-code");
        passwordMode.Content = LoginText.Get("PasswordSignIn");
        codeMode.Content = LoginText.Get("CodeSignIn");
        passwordMode.Style = codeMode.Style = style("LoginModeRadioStyle");
        AutomationProperties.SetAutomationId(passwordMode, "email-password-mode");
        AutomationProperties.SetAutomationId(codeMode, "email-code-mode");
        SlidingModeSelector rail = new(passwordMode, codeMode) { Margin = new(0, 0, 0, 14) };
        emailBody.Children.Add(rail);
        emailBody.Children.Add(Group("Email", email));
        passwordGroup.Children.Add(Group("Password", secret));
        emailBody.Children.Add(passwordGroup);
        Grid codeRow = new();
        codeRow.ColumnDefinitions.Add(new());
        codeRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        codeRow.Children.Add(emailCode);
        Configure(captcha, "SendEmailCode", "send-code", "SmsButtonStyle");
        captcha.MinHeight = 42;
        captcha.MinWidth = 86;
        captcha.MaxWidth = 150;
        captcha.Margin = new(8, 0, 0, 0);
        Grid.SetColumn(captcha, 1);
        codeRow.Children.Add(captcha);
        codeGroup.Children.Add(Group("EmailCode", codeRow));
        emailBody.Children.Add(codeGroup);
        newBody.Children.Add(emailBody);
        remember.Style = style("FormCheckBoxStyle");
        remember.Content = new TextBlock { Text = LoginText.Get("SaveOnDevice"), TextWrapping = TextWrapping.Wrap };
        remember.Margin = new(0, 12, 0, 0);
        AutomationProperties.SetAutomationId(remember, "remember");
        AutomationProperties.SetName(remember, LoginText.Get("SaveOnDevice"));
        newBody.Children.Add(remember);
        newBody.Children.Add(new GlobalAgreementBlock(agreement, style("FormCheckBoxStyle"), model.Region));
        Children.Add(newBody);
        status.Margin = new(0, 10, 0, 4);
        AutomationProperties.SetAutomationId(status, "status");
        AutomationProperties.SetLiveSetting(status, System.Windows.Automation.AutomationLiveSetting.Polite);
        Children.Add(status);
        Configure(signIn, "ContinueSignIn", "submit", "AccentButtonStyle");
        signIn.Margin = new(0, 12, 0, 0);
        Configure(otherAccount, "UseOtherAccount", "other-account");
        otherAccount.Margin = new(0, 6, 0, 0);
        Configure(retry, "RetryConnection", "retry-connection", "SmsButtonStyle");
        Configure(cancelWork, "CancelOperation", "cancel-operation", "SmsButtonStyle");
        cancelWork.Margin = new(0, 14, 0, 0);
        Configure(help, "SignInHelp", "help");
        help.Margin = new(0, 12, 0, 0);
        help.FontSize = 11;
        Children.Add(signIn);
        Children.Add(otherAccount);
        Children.Add(retry);
        Children.Add(cancelWork);

        void Configure(Button button, string key, string id, string styleName = "QuietButtonStyle")
        {
            button.Style = style(styleName);
            button.Content = new TextBlock
                { Text = LoginText.Get(key), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            button.MinHeight = styleName == "QuietButtonStyle" ? 28 : 44;
            button.Padding = new(12, 8, 12, 8);
            button.FontSize = styleName == "AccentButtonStyle" ? 14 : 12;
            AutomationProperties.SetAutomationId(button, id);
            AutomationProperties.SetName(button, LoginText.Get(key));
        }
    }

    private static StackPanel Group(string key, UIElement input)
    {
        StackPanel group = new() { Margin = new(0, 0, 0, 12) };
        group.Children.Add(new TextBlock { Text = LoginText.Get(key), FontSize = 12, Margin = new(0, 0, 0, 7) });
        group.Children.Add(input);
        return group;
    }

    private static Grid ProviderLabel(string asset, string key)
    {
        Grid row = new();
        row.ColumnDefinitions.Add(new() { Width = new(30) });
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new() { Width = new(30) });
        BitmapImage bitmap = new();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new(Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", asset));
        bitmap.EndInit();
        bitmap.Freeze();
        Image image = new()
        {
            Source = bitmap, Width = 20, Height = 20, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        row.Children.Add(image);
        TextBlock text = new()
        {
            Text = LoginText.Get(key), FontSize = 14, FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private void RefreshLayout()
    {
        bool busy = model.IsBusy;
        var page = model.Page;
        savedBody.Visibility = !busy && page == GlobalLoginPage.Saved ? Visibility.Visible : Visibility.Collapsed;
        newBody.Visibility = !busy && page is GlobalLoginPage.Methods or GlobalLoginPage.Email ? Visibility.Visible : Visibility.Collapsed;
        methodsBody.Visibility = page == GlobalLoginPage.Methods ? Visibility.Visible : Visibility.Collapsed;
        emailEntry.Visibility = model.SupportsEmail ? Visibility.Visible : Visibility.Collapsed;
        emailBody.Visibility = page == GlobalLoginPage.Email ? Visibility.Visible : Visibility.Collapsed;
        helpBody.Visibility = !busy && page == GlobalLoginPage.Help ? Visibility.Visible : Visibility.Collapsed;
        back.Visibility = !busy && (page is GlobalLoginPage.Email or GlobalLoginPage.Help || page == GlobalLoginPage.Methods && picker.HasAccounts)
            ? Visibility.Visible : Visibility.Collapsed;
        back.Content = LoginText.Get(page == GlobalLoginPage.Email ? "BackToMethods" : page == GlobalLoginPage.Help ? "BackToSignIn" : "BackToSavedAccounts");
        signIn.Visibility = !busy && (page == GlobalLoginPage.Email || page == GlobalLoginPage.Saved && !picker.IsManaging) ? Visibility.Visible : Visibility.Collapsed;
        otherAccount.Visibility = !busy && page == GlobalLoginPage.Saved && !picker.IsManaging ? Visibility.Visible : Visibility.Collapsed;
        retry.Visibility = !busy && !model.IsPreview && !model.Ready && page == GlobalLoginPage.Methods ? Visibility.Visible : Visibility.Collapsed;
        cancelWork.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        help.Visibility = Visibility.Collapsed;
        status.Visibility = string.IsNullOrWhiteSpace(model.Status) ? Visibility.Collapsed : Visibility.Visible;
        passwordGroup.Visibility = model.UsePassword ? Visibility.Visible : Visibility.Collapsed;
        codeGroup.Visibility = model.UsePassword ? Visibility.Collapsed : Visibility.Visible;
        email.IsEnabled = secret.IsEnabled = emailCode.IsEnabled = passwordMode.IsEnabled = codeMode.IsEnabled = !busy;
        agreement.IsEnabled = remember.IsEnabled = picker.IsEnabled = !busy;
        captcha.Content = model.CaptchaLabel; signIn.Content = model.SubmitLabel;
        signIn.IsDefault = model.SubmitCommand.CanExecute(null) && !picker.IsManaging;
        LayoutChanged?.Invoke();
    }

    private SavedAccountChoice Present(LocalAccount account)
    {
        model.Profiles.TryGetValue(account.Id, out AccountDisplayProfile? profile);
        bool custom = profile?.InitialLabel == null || account.Label != profile.InitialLabel;
        string primary = custom ? account.Label : profile?.Email ?? profile?.Name ?? account.Label;
        string secondary = profile?.Email != null && primary != profile.Email ? profile.Email
            : profile?.Email != null ? profile.Name ?? "" : LoginText.Get("EmailNotProvided") + " · " + LoginText.Get("SavedAccountNumber", picker.Reference(account.Id));
        string source = profile?.Provider switch { 2 => "Google", 1 => "Facebook", 8 => LoginText.Get("Email"), _ => LoginText.Get("LegacyAccount") };
        return new(account, primary, secondary, source);
    }
    internal void SetPreviewProfileScenario(string variant) => model.SetPreviewProfileScenario(variant);
    internal void SetPreviewAccounts(int count) => model.SetPreviewAccounts(count);
}
