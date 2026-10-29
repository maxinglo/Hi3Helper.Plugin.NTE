using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Nte.LoginHost;

internal sealed partial class LoginWindow : Window
{
    private const double CardWidth = 368, CardHeight = 448, SceneWidth = 456, SceneHeight = 484;
    private readonly Canvas scene = new() { Width = SceneWidth, Height = SceneHeight, Background = null };
    private readonly TextBlock title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock subtitle = new() { FontSize = 11, Opacity = .6, Margin = new(0, 7, 0, 0) };
    private readonly Grid content = new() { Margin = new(26, 24, 26, 20) };
    private readonly StackPanel form = new();
    private readonly TextBox phone = new() { Height = 40, MaxLength = 11 };
    private readonly TextBox captcha = new() { Height = 40, MaxLength = 8 };

    private readonly Button sms = new()
        { Content = Localization.LoginText.Get("SendSmsCode"), Height = 40, FontSize = 12 };

    private readonly Button login = new() { Content = Localization.LoginText.Get("SignIn"), Height = 42 };

    private readonly TextBlock status = new()
        { FontSize = 12, Opacity = .85, TextWrapping = TextWrapping.Wrap };

    private readonly StackPanel footer = new();
    private readonly Button closeLogin = new();
    private readonly Border card;
    private readonly Border materialTint = new() { CornerRadius = new(16) };

    private readonly System.Windows.Shapes.Rectangle cachedBackdrop = new()
        { RadiusX = 16, RadiusY = 16, IsHitTestVisible = false };

    private AcrylicBackdrop.Snapshot? backdrop;
    private bool backdropAttempted;
    private int backdropCaptureCount;
    private int backdropRefreshAttempts;
    private readonly BackdropRefreshSchedule backdropRefresh = new();
    private readonly DispatcherTimer backdropTimer = new();
    private readonly Stopwatch backdropClock = new();
    private readonly List<double> backdropCaptureTimes = [];
    private readonly GameCursor gameCursor = new();

    private readonly ImageBrush backdropBrush = new()
        { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill };

    private readonly ScrollViewer scroll;

    private readonly Image mascot;

    // Discovery and theme only. Geometry is driven by SDK and WinEvents.
    private readonly DispatcherTimer tracker = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly nint handle;
    private readonly Viewbox view;
    private readonly WindowNative.WinEventProcedure windowEvents;
    private readonly List<nint> eventHooks = [];
    private bool layoutQueued;
    private int sdkGeometryNotifications, windowGeometryNotifications;
    private int placementChanges;
    private Process? game;
    private nint gameWindow;
    private bool requested, preview, busy, stopping, helpOpen;
    private string currentTheme = "";
    private int attempt;
    private CancellationTokenSource? operation;
    private ProbeAuthenticationClient? client;
    private Func<ProbeIdentityEnvelope, ProbeIdentityEnvelope, CancellationToken, Task>? deliver;
    private TaskCompletionSource<bool>? completion;
    private TaskCompletionSource? failureCompletion;
    private WelcomeToast? welcome;
    private bool automaticResumeInProgress;
    private EventHandler? statusTextChanged;
    private bool cardSizeQueued;

    private void ShowWelcome(string label)
    {
        welcome ??= new WelcomeToast();
        welcome.Present(label, gameWindow, preview ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(4));
    }

    internal LoginWindow(bool exposePreview = false)
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/Nte.LoginHost;component/Styles/Login.Tasks.xaml") });
        Title = Localization.LoginText.Get("MainlandTitle");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = exposePreview;
        ShowActivated = false;
        FontFamily = new("Microsoft YaHei UI");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SetResourceReference(ForegroundProperty, "TextBrush");
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        StackPanel heading = new() { Margin = new(0, 0, 0, 16) };
        heading.Children.Add(title);
        heading.Children.Add(subtitle);
        content.Children.Add(heading);
        Button cancel = closeLogin;
        cancel.Content = "×";
        cancel.Width = cancel.Height = 24;
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        cancel.VerticalAlignment = VerticalAlignment.Top;
        cancel.FontSize = 17;
        cancel.Style = FindStyle("QuietButtonStyle");
        AutomationProperties.SetName(cancel, Localization.LoginText.Get("CancelSignIn"));
        cancel.Click += (_, _) =>
        {
            if (MandatoryNotice || noticeConfirmation) return;
            if (helpOpen)
            {
                ReturnFromNotice();
                return;
            }

            if (failureCompletion != null) failureCompletion.TrySetResult();
            else CancelFlow();
        };
        content.Children.Add(cancel);
        Grid.SetRow(modeRail, 1);
        content.Children.Add(modeRail);
        scroll = new()
        {
            Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Style = FindStyle("LoginScrollViewerStyle")
        };
        Grid.SetRow(scroll, 2);
        content.Children.Add(scroll);
        footer.Margin = new(0, 16, 0, 0);
        status.Margin = new(0, 0, 0, 10);
        footer.Children.Add(status);
        footer.Children.Add(login);
        Grid.SetRow(footer, 3);
        content.Children.Add(footer);
        Grid layers = new();
        layers.Children.Add(cachedBackdrop);
        layers.Children.Add(materialTint);
        layers.Children.Add(content);
        card = new()
        {
            Width = CardWidth, Height = CardHeight, CornerRadius = new(17), BorderThickness = new(1), Child = layers,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = .22 }
        };
        Canvas.SetLeft(card, 10);
        Canvas.SetTop(card, 18);
        scene.Children.Add(card);
        mascot = new()
        {
            Width = 72, Height = 92, Stretch = Stretch.Uniform
        };
        BitmapImage image = new();
        using (FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "question-mascot.png")))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = file;
            image.EndInit();
            image.Freeze();
        }

        mascot.Source = image;
        RenderOptions.SetBitmapScalingMode(mascot, BitmapScalingMode.HighQuality);
        Button mascotButton = new()
        {
            Content = mascot, Width = 76, Height = 96, Padding = new(0), Style = FindStyle("QuietButtonStyle"),
            ToolTip = Localization.LoginText.Get("SignInHelp")
        };
        AutomationProperties.SetName(mascotButton, Localization.LoginText.Get("ReadSignInHelp"));
        AutomationProperties.SetAutomationId(mascotButton, "help");
        mascotButton.Click += (_, _) => ToggleHelp();
        Canvas.SetLeft(mascotButton, 362);
        Canvas.SetTop(mascotButton, 44);
        scene.Children.Add(mascotButton);
        view = new Viewbox
        {
            Child = scene, Stretch = Stretch.Uniform, Resources = Resources,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new(18)
        };
        view.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextBrush");
        view.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, FontFamily);
        Grid cover = new() { Background = new SolidColorBrush(Color.FromArgb(8, 0, 0, 0)) };
        cover.Children.Add(view);
        Content = cover;
        phone.Style = FindStyle("LoginTextBoxStyle");
        captcha.Style = FindStyle("LoginTextBoxStyle");
        login.Style = FindStyle("AccentButtonStyle");
        sms.Style = FindStyle("SmsButtonStyle");
        phone.Height = captcha.Height = 42;
        phone.Padding = new(39, 9, 12, 9);
        captcha.Padding = new(39, 9, 12, 9);
        sms.Height = 42;
        sms.MinWidth = 94;
        sms.MaxWidth = 150;
        sms.HorizontalAlignment = HorizontalAlignment.Right;
        sms.VerticalAlignment = VerticalAlignment.Center;
        sms.Margin = new(8, 0, 0, 0);
        sms.Padding = new(6, 3, 6, 3);
        ConfigureSmsCooldown();
        login.Click += async (_, _) =>
        {
            if (failureCompletion != null) failureCompletion.TrySetResult();
            else if (helpOpen) AcknowledgeNotice();
            else if (!initialized && !preview) await InitializeLoginAsync();
            else if (localMode && selectedAccount == null) await SwitchModeAsync(false);
            else await ExecuteAsync(LoginWithSelectedAccountAsync);
        };
        tracker.Tick += (_, _) => DiscoverAndUpdateTheme();
        backdropTimer.Tick += (_, _) => RefreshBackdrop();
        AddHandler(System.Windows.Input.Mouse.QueryCursorEvent,
            new System.Windows.Input.QueryCursorEventHandler((_, args) =>
            {
                if (!IsVisible || !requested) return;
                if (gameCursor.Value == null) gameCursor.TryObserve(gameWindow, handle);
                if (gameCursor.Value is { } cursor)
                {
                    args.Cursor = cursor;
                    args.Handled = true;
                }
            }), true);
        windowEvents = (_, eventType, window, objectId, childId, _, _) =>
        {
            if (eventType != 3 && (objectId != 0 || childId != 0)) return;
            if (eventType == 3 || window == gameWindow || !WindowNative.IsWindow(gameWindow))
            {
                Interlocked.Increment(ref windowGeometryNotifications);
                QueueLayout();
            }
        };
        Closing += (_, e) =>
        {
            if (!stopping)
            {
                e.Cancel = true;
                if (MandatoryNotice || noticeConfirmation) return;
                if (failureCompletion != null) failureCompletion.TrySetResult();
                else CancelFlow();
            }
        };
        handle = new WindowInteropHelper(this).EnsureHandle();
        WindowNative.SetWindowLongPtr(handle, -16,
            (WindowNative.GetWindowLongPtr(handle, -16) | unchecked((nint)0x80000000)) & ~0x40000000);
        WindowNative.SetWindowLongPtr(handle, -20, exposePreview
            ? (WindowNative.GetWindowLongPtr(handle, -20) | 0x40000) & ~0x88
            : (WindowNative.GetWindowLongPtr(handle, -20) | 0x80) & ~0x40008);
        HwndSource.FromHwnd(handle)?.AddHook(NativeMessage);
        ConfigureAccounts();
        view.SizeChanged += (_, _) => UpdateBackdropViewbox();
        scroll.SizeChanged += (_, _) => QueueBodyWidth();
        form.SizeChanged += (_, _) =>
        {
            QueueBodyWidth();
            QueueCardSize();
        };
        statusTextChanged = (_, _) =>
        {
            status.Visibility = string.IsNullOrWhiteSpace(status.Text) || globalView != null
                ? Visibility.Collapsed
                : Visibility.Visible;
            QueueCardSize();
        };
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(status, statusTextChanged);
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Escape && helpOpen)
            {
                if (noticeConfirmation) ShowLoginNotice();
                else if (!MandatoryNotice) ReturnFromNotice();
                args.Handled = true;
            }
        };
        officialLauncher.Style = FindStyle("SmsButtonStyle");
        officialLauncher.Margin = new(0, 8, 0, 0);
        officialLauncher.MinHeight = 36;
        officialLauncher.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(officialLauncher, "official-launcher");
        officialLauncher.Click += (_, _) => ChooseOfficialLauncher();
        footer.Children.Add(officialLauncher);
        ShowForm();
        ApplyClockTheme();
    }

    private Style FindStyle(string name) => (Style)FindResource(name);

    private static void Add(StackPanel parent, FrameworkElement element, double bottom = 14)
    {
        element.Margin = new(0, 0, 0, bottom);
        parent.Children.Add(element);
    }

    private static StackPanel Group(string title, UIElement field)
    {
        StackPanel group = new();
        group.Children.Add(new TextBlock { Text = title, FontSize = 12, Opacity = .85, Margin = new(0, 0, 0, 8) });
        group.Children.Add(field);
        return group;
    }

    private Grid PhoneField()
    {
        Grid field = new();
        field.Children.Add(phone);
        TextBlock hint = new()
        {
            Text = Localization.LoginText.Get("PhonePlaceholder"), FontSize = 13, Opacity = .42,
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new(39, 0, 12, 0)
        };
        hint.Visibility = phone.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        phone.TextChanged += (_, _) =>
            hint.Visibility = phone.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        field.Children.Add(hint);
        field.Children.Add(FieldIcon("\uE8EA"));
        return field;
    }

    private Grid CodeField()
    {
        Grid field = new();
        field.ColumnDefinitions.Add(new());
        field.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        field.Children.Add(captcha);
        TextBlock hint = new()
        {
            Text = Localization.LoginText.Get("CodePlaceholder"), FontSize = 13, Opacity = .42,
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new(39, 0, 12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        hint.Visibility = captcha.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        captcha.TextChanged += (_, _) =>
            hint.Visibility = captcha.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        field.Children.Add(hint);
        field.Children.Add(FieldIcon("\uE72E"));
        Grid.SetColumn(sms, 1);
        field.Children.Add(sms);
        return field;
    }

    private static TextBlock FieldIcon(string glyph) => new()
    {
        Text = glyph, FontFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Width = 18,
        Opacity = .65, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
        TextAlignment = TextAlignment.Center, Margin = new(13, 0, 0, 0)
    };

    private void QueueBodyWidth() => Dispatcher.BeginInvoke(() =>
    {
        if (scroll.ViewportWidth > 0 && (double.IsNaN(form.Width) || Math.Abs(form.Width - scroll.ViewportWidth) > .1))
            form.Width = scroll.ViewportWidth;
    }, DispatcherPriority.Loaded);

    private Grid? phoneField, codeField;
    private StackPanel? phoneGroup, verificationGroup;

    private void UpdateCardSize()
    {
        // Measure at the actual inner card width with no scrollbar. A scrollbar
        // from the previous page must not steal width and create another wrap.
        form.Width = double.NaN;
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        content.Measure(new Size(CardWidth - card.BorderThickness.Left - card.BorderThickness.Right,
            double.PositiveInfinity));
        double naturalHeight = Math.Ceiling(content.DesiredSize.Height) + card.BorderThickness.Top +
                               card.BorderThickness.Bottom + 4;
        double height = Math.Clamp(naturalHeight, 260, 610);
        scroll.VerticalScrollBarVisibility =
            naturalHeight > 610 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        if (Math.Abs(card.Height - height) < .5) return;
        card.Height = height;
        scene.Height = height + SceneHeight - CardHeight;
        if (requested) QueueLayout();
    }

    private void QueueCardSize()
    {
        if (cardSizeQueued || stopping) return;
        cardSizeQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            cardSizeQueued = false;
            if (!stopping) UpdateCardSize();
        }, DispatcherPriority.Loaded);
    }

    private void ShowForm()
    {
        form.Children.Clear();
        helpOpen = false;
        title.Text = Localization.LoginText.Get("MainlandTitle");
        subtitle.Text = "";
        subtitle.Visibility = Visibility.Collapsed;
        footer.Visibility = Visibility.Visible;
        modeRail.Visibility = Visibility.Collapsed;
        status.Visibility = string.IsNullOrWhiteSpace(status.Text) ? Visibility.Collapsed : Visibility.Visible;
        AddAccountTabs();
        if (localMode)
        {
            BuildAccounts();
            RefreshMainlandNavigation();
            UpdateCardSize();
            QueueBodyWidth();
            scroll.ScrollToTop();
            return;
        }

        phoneField ??= PhoneField();
        codeField ??= CodeField();
        phoneGroup ??= Group(Localization.LoginText.Get("Phone"), phoneField);
        verificationGroup ??= Group(Localization.LoginText.Get("SmsCode"), codeField);
        Add(form, phoneGroup, 18);
        Add(form, verificationGroup, 14);
        AddAccountSaveFields();
        login.Content = !initialized && !preview
            ? Localization.LoginText.Get("RetryConnection")
            : Localization.LoginText.Get("SignIn");
        login.IsEnabled = !busy && (preview || initialized || requested);
        RefreshMainlandNavigation();
        UpdateCardSize();
        QueueBodyWidth();
        scroll.ScrollToTop();
    }

    private void ToggleHelp()
    {
        if (MandatoryNotice || noticeConfirmation) return;
        if (helpOpen)
        {
            ReturnFromNotice();
            return;
        }

        noticeGlobal = globalView != null;
        ShowLoginNotice();
    }

    internal async Task<bool> RequestAsync(ProbeAuthenticationClient api,
        Func<ProbeIdentityEnvelope, ProbeIdentityEnvelope, CancellationToken, Task> submit, CancellationToken token,
        Func<CancellationToken, Task>? reset = null)
    {
        CancelFlow();
        await activeWork;
        client = api;
        deliver = submit;
        resetAdmission = reset;
        requestToken = token;
        operation?.Dispose();
        operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        requested = true;
        preview = false;
        IReadOnlyList<Nte.LoginHost.Accounts.LocalAccount> savedAccounts = accounts.List();
        await LoadMainlandDisplaysAsync(token);
        localMode = savedAccounts.Count > 0;
        initialized = false;
        bool resumeLast = false;
        int resumeGeneration = attempt;
        Guid? resumeAccount = null;
        try
        {
            await rememberedChange;
            Guid? last = await remembered.ReadAsync(token);
            selectedAccount = Nte.LoginHost.Accounts.RememberedLogin.Resolve(last, remembered.HasState, savedAccounts);
            resumeLast = selectedAccount != null && !autoResumeDisabled;
            resumeAccount = selectedAccount?.Id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or CryptographicException)
        {
            autoResumeDisabled = true;
        }

        ResetBackdrop();
        using CancellationTokenRegistration registration =
            token.Register(() => Dispatcher.BeginInvoke(CancelForGameExit));
        automaticResumeInProgress = resumeLast;
        if (resumeLast)
        {
            welcome ??= new WelcomeToast();
            welcome.PresentMessage(Localization.LoginText.Get("RestoringLastAccount"), gameWindow,
                TimeSpan.FromSeconds(60));
        }

        ShowForm();
        Track();
        try
        {
            await InitializeLoginAsync();
            if (resumeLast && initialized && requested && attempt == resumeGeneration && localMode && !helpOpen &&
                selectedAccount?.Id == resumeAccount)
            {
                automaticAttempt = true;
                await ExecuteAsync(LoginWithSelectedAccountAsync);
            }
        }
        finally
        {
            automaticAttempt = automaticResumeInProgress = false;
            if (requested) welcome?.Dismiss();
            Track();
        }

        return await completion.Task;
    }

    private Task InitializeLoginAsync() => ExecuteAsync(async (active, cancel, generation) =>
    {
        status.Text = Localization.LoginText.Get("InitializingSignIn");
        experimentalHello = ReadProtectionPreference();
        await active.InitializeAsync(cancel);
        RequireCurrent(generation, cancel);
        initialized = true;
        status.Text = "";
        ShowForm();
    }, true);

    internal void Attach(int pid)
    {
        foreach (nint hook in eventHooks) WindowNative.UnhookWinEvent(hook);
        eventHooks.Clear();
        game?.Dispose();
        game = Process.GetProcessById(pid);
        foreach ((uint low, uint high, uint process) in new[]
                     { (0x8000u, 0x800Bu, (uint)pid), (0xAu, 0x17u, (uint)pid), (3u, 3u, 0u) })
        {
            nint hook = WindowNative.SetWinEventHook(low, high, 0, windowEvents, process, 0, 2);
            if (hook == 0) throw new System.ComponentModel.Win32Exception();
            eventHooks.Add(hook);
        }

        tracker.Start();
    }

    internal async Task PreviewAsync(int pid, bool welcomeOnly = false)
    {
        Attach(pid);
        preview = requested = initialized = true;
        SetMainlandPreviewAccounts(3);
        ResetBackdrop();
        status.Text = "";
        SetAccountControlsEnabled(true);
        ShowForm();
        Track();
        if (welcomeOnly)
        {
            requested = false;
            HidePresented();
            ShowWelcome(Localization.LoginText.Get("PreviewMainAccount"));
        }

        await game!.WaitForExitAsync();
    }

    private Task ExecuteAsync(Func<ProbeAuthenticationClient, CancellationToken, int, Task> action,
        bool initialize = false)
    {
        if (busy) return Task.CompletedTask;
        if (preview)
        {
            status.Text = Localization.LoginText.Get("PreviewNoAuthentication");
            return Task.CompletedTask;
        }

        if (client == null || operation == null || (!initialize && !initialized)) return Task.CompletedTask;
        activeWork = RunActionAsync(action, client, operation.Token, attempt);
        return activeWork;
    }

    private async Task RunActionAsync(Func<ProbeAuthenticationClient, CancellationToken, int, Task> action,
        ProbeAuthenticationClient api, CancellationToken token, int generation)
    {
        busy = true;
        SetAccountControlsEnabled(false);
        try
        {
            await action(api, token, generation);
        }
        catch (ProbeAuthenticationException ex)
        {
            if (generation == attempt)
            {
                if (ex.Phase == "channel-validation-rejected")
                {
                    localMode = false;
                    phone.Clear();
                    captcha.Clear();
                    ShowForm();
                }

                status.Text = ex.LocalExplanation ?? Friendly(ex.Phase);
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == attempt) status.Text = Localization.LoginText.Get("OperationCancelled");
        }
        catch (Nte.LoginHost.Accounts.HelloProtectionException ex)
        {
            if (generation == attempt)
                status.Text = Localization.LoginText.Get("HelloProtectionFailed", $"0x{ex.NativeError:X8}");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            if (generation == attempt) status.Text = Localization.LoginText.Get("AccountUnlockFailed");
        }
        catch (InvalidDataException)
        {
            if (generation == attempt) status.Text = Localization.LoginText.Get("SavedAccountUnavailable");
        }
        catch (Exception)
        {
            if (generation == attempt) status.Text = Localization.LoginText.Get("SignInIncomplete");
        }
        finally
        {
            if (generation == attempt)
            {
                busy = false;
                SetAccountControlsEnabled(initialized);
                if (!initialized && !preview && requested && !helpOpen)
                {
                    login.Content = Localization.LoginText.Get("RetryConnection");
                    login.IsEnabled = true;
                }
            }
        }
    }

    private void CancelFlow()
    {
        noticeCompletion?.TrySetResult(false);
        welcome?.Dismiss();
        automaticResumeInProgress = false;
        attempt++;
        requested = false;
        operation?.Cancel();
        completion?.TrySetResult(false);
        busy = false;
        phone.Clear();
        captcha.Clear();
        globalView?.Cancel();
        HidePresented();
        ResetBackdrop();
        UpdateSmsButton();
    }

    internal void CancelForGameExit() => CancelFlow();

    internal void ReportServiceFailure(string text) => Dispatcher.BeginInvoke(() =>
    {
        if (globalView != null) globalView.ShowNotice(text);
        else status.Text = text;
    });

    internal void NotifySdkGeometryChanged()
    {
        Interlocked.Increment(ref sdkGeometryNotifications);
        QueueLayout();
    }

    private void QueueLayout()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(QueueLayout, DispatcherPriority.Send);
            return;
        }

        if (stopping || layoutQueued) return;
        layoutQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            layoutQueued = false;
            Track();
        }, DispatcherPriority.Send);
    }

    private nint NativeMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x2E0 or 0x7E) QueueLayout();
        if (message == 0x20 && IsVisible && requested &&
            gameCursor.RequestSynchronize(gameWindow, handle, () => System.Windows.Input.Mouse.UpdateCursor()))
        {
            handled = true;
            return 1;
        }

        return 0;
    }

    private void DiscoverAndUpdateTheme()
    {
        ApplyClockTheme();
        if (welcome?.Active == true && game != null && !game.HasExited)
            gameWindow = WindowNative.FindGameWindow(game.Id);
        welcome?.Track(gameWindow);
        if (game == null || game.HasExited || !requested)
        {
            HidePresented();
            return;
        }

        nint target = WindowNative.FindGameWindow(game.Id);
        if (target != gameWindow)
        {
            gameWindow = target;
            QueueLayout();
        }

        if (preview) _ = ProcessPreviewActionAsync();
        if (preview) WritePreviewState();
    }

    private void Track()
    {
        ApplyClockTheme();
        if (welcome?.Active == true && game != null && !game.HasExited)
            gameWindow = WindowNative.FindGameWindow(game.Id);
        welcome?.Track(gameWindow);
        if (automaticResumeInProgress)
        {
            HidePresented();
            return;
        }

        if (game == null || game.HasExited || !requested)
        {
            HidePresented();
            return;
        }

        nint target = WindowNative.FindGameWindow(game.Id);
        if (target == 0 || WindowNative.IsIconic(target))
        {
            HidePresented();
            return;
        }

        gameWindow = target;
        nint foreground = WindowNative.GetForegroundWindow();
        WindowNative.GetWindowThreadProcessId(foreground, out uint foregroundProcess);
        // A native Hello dialog runs in a system process. Keep its owner alive
        // and visible during that operation instead of hiding it on activation.
        if (!preview && helloPrompts == 0 && foreground != target && foregroundProcess != Environment.ProcessId)
        {
            HidePresented();
            return;
        }

        WindowNative.Rect clientRect = WindowNative.ScreenClient(target);
        if (clientRect.Width < 320 || clientRect.Height < 180)
        {
            HidePresented();
            return;
        }

        if (!backdropAttempted && !IsVisible)
        {
            backdropAttempted = true;
            backdropClock.Restart();
            backdropRefresh.Start(TimeSpan.Zero);
            if (!SystemParameters.HighContrast)
            {
                backdrop = AcrylicBackdrop.Capture(target, clientRect);
                if (backdrop != null)
                {
                    backdropCaptureCount++;
                    backdropCaptureTimes.Add(backdropClock.Elapsed.TotalSeconds);
                    backdropBrush.ImageSource = backdrop.Image;
                    cachedBackdrop.Fill = backdropBrush;
                }
            }

            UpdateMaterialTint();
            ScheduleBackdropRefresh();
        }

        if (WindowNative.GetWindow(handle, 4) != target)
        {
            System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0);
            WindowNative.SetWindowLongPtr(handle, -8, target);
            if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() != 0)
                throw new System.ComponentModel.Win32Exception();
        }

        WindowNative.Rect previous = WindowNative.ScreenClient(handle);
        if (!IsVisible) Show();
        if (previous.Left != clientRect.Left || previous.Top != clientRect.Top || previous.Width != clientRect.Width ||
            previous.Height != clientRect.Height)
        {
            if (!WindowNative.SetWindowPos(handle, 0, clientRect.Left, clientRect.Top, clientRect.Width,
                    clientRect.Height, 0x214))
                throw new System.ComponentModel.Win32Exception();
            placementChanges++;
        }

        double scale = Math.Max(1, WindowNative.GetDpiForWindow(handle) / 96d);
        double width = Math.Min(SceneWidth, (clientRect.Width / scale - 36) * .62);
        double height = Math.Min(scene.Height, clientRect.Height / scale - 28);
        width = Math.Min(width, height * SceneWidth / scene.Height);
        view.Width = width;
        view.Height = width * scene.Height / SceneWidth;
        UpdateBackdropViewbox();
        if (gameCursor.Value == null) gameCursor.TryObserve(target, handle);
        if (preview) WritePreviewState();
    }

    private void ApplyClockTheme() => ApplyTheme(LoginTheme.ForLocalTime(DateTime.Now));

    private void ApplyTheme(string theme)
    {
        if (theme == currentTheme) return;
        currentTheme = theme;
        bool dark = theme == "night", warm = theme == "sunset";
        Resources["TextBrush"] = new SolidColorBrush(dark ? Color.FromRgb(232, 241, 247) : Color.FromRgb(31, 45, 53));
        Resources["AccentBrush"] = new SolidColorBrush(warm ? Color.FromRgb(154, 105, 47) :
            dark ? Color.FromRgb(39, 154, 175) : Color.FromRgb(28, 123, 143));
        Resources["InputBrush"] =
            new SolidColorBrush(dark ? Color.FromArgb(22, 218, 234, 246) : Color.FromArgb(178, 255, 255, 255));
        Resources["InputBorderBrush"] =
            new SolidColorBrush(dark ? Color.FromArgb(25, 232, 243, 250) : Color.FromArgb(25, 55, 90, 105));
        Resources["GlobalPopupBrush"] =
            new SolidColorBrush(dark ? Color.FromRgb(31, 45, 64) : Color.FromRgb(246, 250, 252));
        UpdateMaterialTint();
        card.BorderBrush =
            new SolidColorBrush(dark ? Color.FromArgb(35, 222, 237, 248) : Color.FromArgb(40, 100, 123, 134));
    }

    private void WritePreviewState()
    {
        var previewPixels = PreviewBackdropProbe();
        WindowNative.Rect bounds = WindowNative.ScreenClient(handle);
        string path = Path.Combine(AppContext.BaseDirectory, "preview-layout.json"), temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            theme = currentTheme, themeSource = "system-local-time", themeDerivedFromPixels = false,
            gamePid = game?.Id, hostPid = Environment.ProcessId, width = bounds.Width, height = bounds.Height,
            xamlWidth = card.ActualWidth, xamlHeight = card.ActualHeight,
            rasterScale = VisualTreeHelper.GetDpi(view).DpiScaleX,
            formDesiredHeight = form.DesiredSize.Height, formAvailableHeight = scroll.ViewportHeight,
            formWidth = form.ActualWidth, viewportWidth = scroll.ViewportWidth, footerWidth = footer.ActualWidth,
            scrollable = scroll.ScrollableHeight > 0, slider = true, fieldIcons = 2, duplicateHelpLink = false,
            modernSaveToggle = true, saveEnabled = saveAccount.IsChecked == true, labelEditorOnMain = false,
            experimentalHello,
            smsCooldown = new
            {
                enabled = sms.IsEnabled, remainingSeconds = smsRemainingSeconds, text = sms.Content,
                normalTextColor = Equals(sms.Foreground, Resources["TextBrush"]),
                surfaceOpacity = (sms.Template.FindName("Surface", sms) as Border)?.Opacity
            },
            savedAccounts = new
            {
                count = previewAccounts.Count, ids = previewAccounts.Select(a => a.Id).ToArray(),
                selected = selectedAccount?.Id
            },
            mascotReady = mascot.Source != null, mascotMirrored = mascot.RenderTransform.Value.M11 < 0,
            segmentFocusOutline = false,
            gameCursorSynchronized = gameCursor.Value != null, cursorSynchronizations = gameCursor.Synchronizations,
            renderer = "WPF-owned-client-cover", nativeWindowsForLogin = 1,
            nativeChild = false, ownerWindow = (long)WindowNative.GetWindow(handle, 4), gameWindow = (long)gameWindow,
            fullClientCover = true, geometryPolling = false, placementChanges, sdkGeometryNotifications,
            windowGeometryNotifications,
            accountRequests = 0, helloInvoked = false, acrylic = new
            {
                cached = backdrop != null, captureCount = backdropCaptureCount, captureAttempted = backdropAttempted,
                refreshAttempts = backdropRefreshAttempts, refreshAtSeconds = new[] { 5, 15, 60 },
                captureTimesSeconds = backdropCaptureTimes.ToArray(),
                preparationMs = backdrop?.PreparationMs, cacheBytes = backdrop?.Bytes ?? 0,
                previewSampleSpread = previewPixels.Spread, previewSamples = previewPixels.Samples,
                continuousCapture = false, savedToDisk = false
            }
        }));
        // A fixture can be reading the last snapshot when this timer fires.
        // Keep that snapshot and retry on the next tick instead of ending UI.
        try
        {
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private (int Spread, string[] Samples) PreviewBackdropProbe()
    {
        if (backdrop == null) return (-1, []);
        int[] low = [255, 255, 255], high = [0, 0, 0];
        List<string> samples = [];
        WindowNative.Rect client = WindowNative.ScreenClient(gameWindow);
        bool hasCursor = WindowNative.GetCursorPos(out WindowNative.Point cursor);
        byte[] pixel = new byte[4];
        foreach (double x in new[] { .1, .5, .8, .9 })
        foreach (double y in new[] { .2, .5, .8 })
        {
            // Desktop cursor decorations can be separate windows and therefore
            // remain after our cover is excluded. They are not the login card;
            // keep the uniform-background fixture independent of cursor position.
            if (hasCursor && Math.Abs(client.Left + x * client.Width - cursor.X) < 64 &&
                Math.Abs(client.Top + y * client.Height - cursor.Y) < 64)
            {
                samples.Add($"{x},{y}:cursor-area-excluded");
                continue;
            }

            backdrop.Image.CopyPixels(
                new Int32Rect((int)(x * (backdrop.Image.PixelWidth - 1)), (int)(y * (backdrop.Image.PixelHeight - 1)),
                    1, 1), pixel, 4, 0);
            samples.Add($"{x},{y}:{pixel[2]},{pixel[1]},{pixel[0]}");
            for (int channel = 0; channel < 3; channel++)
            {
                low[channel] = Math.Min(low[channel], pixel[channel]);
                high[channel] = Math.Max(high[channel], pixel[channel]);
            }
        }

        return (Enumerable.Range(0, 3).Max(channel => high[channel] - low[channel]), samples.ToArray());
    }

    private void HidePresented()
    {
        if (failureCompletion != null && !stopping) return;
        if (IsVisible) Hide();
    }

    private void ResetBackdrop()
    {
        backdropTimer.Stop();
        backdropRefresh.Stop();
        backdropClock.Reset();
        backdropCaptureTimes.Clear();
        backdropCaptureCount = backdropRefreshAttempts = 0;
        backdrop = null;
        backdropAttempted = false;
        backdropBrush.ImageSource = null;
        cachedBackdrop.Fill = null;
        UpdateMaterialTint();
    }

    private void ScheduleBackdropRefresh()
    {
        backdropTimer.Stop();
        if (SystemParameters.HighContrast || !requested || stopping) return;
        if (backdropRefresh.DelayUntilNext(backdropClock.Elapsed) is not TimeSpan delay) return;
        backdropTimer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1);
        backdropTimer.Start();
    }

    private void RefreshBackdrop()
    {
        backdropTimer.Stop();
        if (!requested || stopping || SystemParameters.HighContrast || game == null || game.HasExited) return;
        nint foreground = WindowNative.GetForegroundWindow();
        if (!IsVisible || gameWindow == 0 || WindowNative.IsIconic(gameWindow) ||
            (foreground != gameWindow && foreground != handle))
        {
            ScheduleBackdropRefresh();
            return;
        }

        if (backdropRefresh.TryConsumeDue(backdropClock.Elapsed))
        {
            backdropRefreshAttempts++;
            AcrylicBackdrop.Snapshot? refreshed =
                AcrylicBackdrop.Capture(gameWindow, WindowNative.ScreenClient(gameWindow), handle);
            if (refreshed != null)
            {
                backdrop = refreshed;
                backdropCaptureCount++;
                backdropCaptureTimes.Add(backdropClock.Elapsed.TotalSeconds);
                backdropBrush.ImageSource = refreshed.Image;
                cachedBackdrop.Fill = backdropBrush;
                UpdateMaterialTint();
                UpdateBackdropViewbox();
            }
        }

        ScheduleBackdropRefresh();
        if (preview) WritePreviewState();
    }

    private void UpdateMaterialTint()
    {
        bool dark = currentTheme == "night",
            warm = currentTheme == "sunset",
            frosted = backdrop != null && !SystemParameters.HighContrast;
        byte opacity = frosted ? (byte)(dark ? 138 : 178) : (byte)248;
        materialTint.Background = new SolidColorBrush(dark ? Color.FromArgb(opacity, 25, 39, 60) :
            warm ? Color.FromArgb(opacity, 252, 237, 218) : Color.FromArgb(opacity, 239, 247, 253));
        card.Background = frosted ? Brushes.Transparent : materialTint.Background;
    }

    private void UpdateBackdropViewbox()
    {
        if (backdrop == null || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0 || card.ActualWidth <= 0 ||
            card.ActualHeight <= 0) return;
        Point origin = card.TranslatePoint(default, this);
        Point corner = card.TranslatePoint(new Point(card.ActualWidth, card.ActualHeight), this);
        double x = Math.Clamp(origin.X / ActualWidth, 0, .9999), y = Math.Clamp(origin.Y / ActualHeight, 0, .9999);
        backdropBrush.Viewbox = new Rect(x, y, Math.Clamp((corner.X - origin.X) / ActualWidth, .0001, 1 - x),
            Math.Clamp((corner.Y - origin.Y) / ActualHeight, .0001, 1 - y));
    }

    internal Task ShowFailureAsync(string text)
    {
        requested = false;
        ResetBackdrop();
        form.Children.Clear();
        Add(form, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        modeRail.Visibility = status.Visibility = Visibility.Collapsed;
        title.Text = Localization.LoginText.Get("ComponentUnavailable");
        subtitle.Text = Localization.LoginText.Get("CheckStartup");
        subtitle.Visibility = Visibility.Visible;
        failureCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        login.Content = Localization.LoginText.Get("Close");
        otherMainlandAccount.Visibility = officialLauncher.Visibility = Visibility.Collapsed;
        login.IsEnabled = true;
        view.Width = SceneWidth;
        view.Height = SceneHeight;
        Width = SceneWidth;
        Height = SceneHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Show();
        return failureCompletion.Task;
    }

    internal void Shutdown()
    {
        stopping = true;
        tracker.Stop();
        if (statusTextChanged != null)
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                .RemoveValueChanged(status, statusTextChanged);
        smsTimer.Stop();
        CancelFlow();
        welcome?.Stop();
        ResetBackdrop();
        foreach (nint hook in eventHooks) WindowNative.UnhookWinEvent(hook);
        eventHooks.Clear();
        HwndSource.FromHwnd(handle)?.RemoveHook(NativeMessage);
        gameCursor.Dispose();
        WindowNative.SetWindowLongPtr(handle, -8, 0);
        game?.Dispose();
        Close();
        GC.KeepAlive(windowEvents);
    }

    private static string Friendly(string phase) => Localization.LoginText.Get(phase switch
    {
        "invalid-mainland-phone" => "InvalidMainlandPhone",
        "invalid-captcha" => "InvalidSmsCode",
        "sms-cooldown" => "SmsCooldown",
        "password-state-unavailable" or "invalid-password-state" => "PasswordStateUnavailable",
        "server-rejected" or "channel-login-rejected" or "game-login-rejected" => "SignInRejected",
        "channel-validation-rejected" => "SavedCredentialsRejected",
        "channel-validation-network-failure" => "SavedValidationNetworkFailure",
        "channel-validation-request-rejected" => "SavedValidationRejected",
        "channel-validation-unknown-response" => "SavedValidationUnknown",
        "channel-validation-official-interaction" => "OfficialInteractionRequired",
        "login-preferences-unavailable" => "ProtectionSettingsUnavailable",
        _ => "SignInServiceUnavailable"
    });
}
