using System.Windows;
using Nte.Login;
using Nte.LoginHost.Localization;

namespace Nte.LoginHost;

internal sealed partial class LoginWindow
{
    private GlobalLoginView? globalView;

    internal async Task<bool> RequestGlobalAsync(NteGlobalAuthentication api,
        Func<NteGlobalIdentity, CancellationToken, Task> submit, CancellationToken token,
        GlobalAccountStore? accounts = null, bool previewMode = false, string? notice = null)
    {
        CancelFlow();
        await activeWork;
        using GlobalLoginView content = new(api, submit, FindStyle, accounts, handle, previewMode, notice,
            (mayPrompt, action) => WithGlobalAccountOwnerAsync(mayPrompt, _ => action()));
        globalView = content;
        Title = LoginText.Get("GlobalTitle");
        content.LayoutChanged += () =>
        {
            QueueCardSize();
            QueueBodyWidth();
            Track();
        };
        requested = true;
        footer.Visibility = Visibility.Collapsed;
        login.IsDefault = false;
        ResetBackdrop();
        form.Children.Clear();
        Add(form, content, 0);
        title.Text = LoginText.Get("GlobalTitle");
        subtitle.Visibility = modeRail.Visibility = status.Visibility = login.Visibility = Visibility.Collapsed;
        UpdateCardSize();
        QueueBodyWidth();
        Track();
        try
        {
            return await content.RunAsync(token);
        }
        finally
        {
            globalView = null;
            requested = false;
            HidePresented();
            ResetBackdrop();
            Track();
            login.Visibility = Visibility.Visible;
            footer.Visibility = Visibility.Visible;
        }
    }

    internal async Task WithGlobalAccountOwnerAsync(bool mayPrompt, Func<nint, Task> action)
    {
        if (!mayPrompt)
        {
            await action(handle);
            return;
        }

        bool transient = !requested;
        Visibility previousLogin = login.Visibility;
        Visibility previousFooter = footer.Visibility;
        if (transient)
        {
            requested = true;
            Title = LoginText.Get("GlobalTitle");
            footer.Visibility = Visibility.Collapsed;
            title.Text = LoginText.Get("GlobalTitle");
            form.Children.Clear();
            Add(form, new System.Windows.Controls.TextBlock
            {
                Text = LoginText.Get("UnlockAccountWithHello"), FontSize = 13, TextWrapping = TextWrapping.Wrap
            }, 0);
            subtitle.Visibility = modeRail.Visibility = status.Visibility = login.Visibility = Visibility.Collapsed;
            UpdateCardSize();
            QueueBodyWidth();
        }

        try
        {
            await WithHelloOwnerAsync(true, async () =>
            {
                await action(handle);
                return true;
            });
        }
        finally
        {
            if (transient)
            {
                requested = false;
                login.Visibility = previousLogin;
                footer.Visibility = previousFooter;
                HidePresented();
                ResetBackdrop();
                Track();
            }
        }
    }

    internal async Task PreviewGlobalAsync(int pid)
    {
        Attach(pid);
        preview = true;
        using NteGlobalApplicationConfiguration config = new(new byte[40], "com.hottagames.nte",
            new("https://mapi.pwsdk.com"), "global-sg");
        using NteGlobalAuthentication api = new(config, "preview");
        using CancellationTokenSource lifetime = new();
        Task formTask = RequestGlobalAsync(api, (_, _) => Task.CompletedTask, lifetime.Token, previewMode: true);
        await game!.WaitForExitAsync();
        lifetime.Cancel();
        await formTask;
    }
}
