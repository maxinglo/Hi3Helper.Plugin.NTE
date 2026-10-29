using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;

internal sealed class UiSession : IDisposable
{
    internal const BindingFlags Members =
        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    private readonly Assembly assembly;
    private readonly string directory, root, region, language;
    private readonly Task preview;
    private readonly Dictionary<string, DependencyObject> controls = [];
    private long revision;
    private object? promptProbe;
    internal Window Window { get; }
    internal string HostSha256 { get; }
    private object? GlobalView => Field(Window, "globalView");

    internal UiSession(string hostDirectory, string directory, int fixturePid, string region, string language,
        string root)
    {
        this.directory = directory;
        this.root = root;
        this.region = region;
        this.language = language;
        string host = Path.Combine(hostDirectory, "Nte.LoginHost.dll");
        HostSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(host)));
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string candidate = Path.Combine(hostDirectory, name.Name + ".dll");
            return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
        };
        assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(host);
        if (assembly.GetName().Name != "Nte.LoginHost") throw new InvalidDataException("Unexpected preview assembly.");
        Call(assembly.GetType("Nte.LoginHost.Localization.LoginText", true)!, "UseLanguage", language);
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/Nte.LoginHost;component/Styles/Collapse.Adapted.xaml") });
        Window = (Window)Activator.CreateInstance(assembly.GetType("Nte.LoginHost.LoginWindow", true)!, Members, null,
            [true], null)!;
        preview = (Task)Call(Window, region == "Global" ? "PreviewGlobalAsync" : "PreviewAsync",
            region == "Global" ? [fixturePid] : [fixturePid, false])!;
        RequirePreview();
        _ = preview.ContinueWith(
            _ => Window.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background),
            TaskScheduler.Default);
    }

    private void RequirePreview()
    {
        if (Field(Window, "preview") is not true || (GlobalView is { } global && Field(global, "preview") is not true))
            throw new InvalidOperationException("Only synthetic, non-authenticating previews can be controlled.");
    }

    internal void Apply(JsonElement command)
    {
        RequirePreview();
        string action = command.GetProperty("action").GetString()!;
        if (action is "state" or "capture" or "close") return;
        if (action == "scenario")
        {
            Scenario(command);
            return;
        }

        if (action == "prompt-probe")
        {
            promptProbe = HelloWindowProbe.Run(assembly, Window, command);
            return;
        }

        if (!command.TryGetProperty("revision", out JsonElement version) || version.GetInt64() != revision)
            throw new InvalidOperationException("stale-revision");
        if (!controls.TryGetValue(command.GetProperty("node").GetString()!, out DependencyObject? element) ||
            (element is FrameworkElement frame && (!frame.IsVisible || !frame.IsEnabled)) ||
            (element is Hyperlink link && !link.IsEnabled))
            throw new InvalidOperationException("unavailable-control");
        switch (action)
        {
            case "invoke" when element is Hyperlink:
                throw new InvalidOperationException("external-navigation-blocked");
            case "invoke" when element is ButtonBase button:
                if (Role(button) == "terms") throw new InvalidOperationException("external-navigation-blocked");
                if (button is RadioButton radio) radio.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                else if (button is ToggleButton option)
                    option.SetCurrentValue(ToggleButton.IsCheckedProperty, option.IsChecked != true);
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                break;
            case "toggle" when element is ToggleButton toggle && toggle is not RadioButton:
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty,
                    command.TryGetProperty("checked", out JsonElement check)
                        ? check.GetBoolean()
                        : toggle.IsChecked != true);
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                break;
            case "fill" when element is TextBox input:
                input.Text = BoundedText(command); break;
            case "fill" when element is PasswordBox password:
                password.Password = BoundedText(command); break;
            case "select" when element is Selector selector:
                int index = command.GetProperty("index").GetInt32();
                if (index < 0 || index >= selector.Items.Count) throw new InvalidDataException("selection-index");
                selector.SelectedIndex = index;
                break;
            case "focus" when element is UIElement focus: focus.Focus(); break;
            case "focus" when element is ContentElement focus: focus.Focus(); break;
            case "key" when element is UIElement target:
                if (!Enum.TryParse(command.GetProperty("key").GetString(), out Key key) || key is not (Key.Enter
                        or Key.Escape or Key.Up or Key.Down or Key.Left or Key.Right or Key.Tab or Key.Space))
                    throw new InvalidDataException("unsupported-key");
                KeyEventArgs args = new(Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(Window) ?? throw new InvalidOperationException("preview-source"),
                        Environment.TickCount, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                target.RaiseEvent(args);
                if (!args.Handled)
                {
                    args.RoutedEvent = Keyboard.KeyDownEvent;
                    target.RaiseEvent(args);
                }

                break;
            default: throw new InvalidDataException("unsupported-action-for-control");
        }
    }

    private static string BoundedText(JsonElement command)
    {
        string text = command.GetProperty("value").GetString()!;
        return text.Length <= 256 && !text.Contains('\0') ? text : throw new InvalidDataException("input-size-bound");
    }

    private void Scenario(JsonElement command)
    {
        if (command.TryGetProperty("firstUseNotice", out JsonElement firstUse) && firstUse.GetBoolean())
            Call(Window, "PreviewFirstUseNotice");
        if (command.TryGetProperty("accountCount", out JsonElement count))
        {
            int length = count.GetInt32();
            if (length is < 0 or > 16) throw new InvalidDataException("account-count");
            if (GlobalView is { } global) Call(global, "SetPreviewAccounts", length);
            else Call(Window, "SetMainlandPreviewAccounts", length);
        }

        if (command.TryGetProperty("profileVariant", out JsonElement variant))
        {
            if (GlobalView is not { } global) throw new InvalidDataException("profile-variant-region");
            string value = variant.GetString()!;
            if (value is not ("legacy" or "missing" or "legacy-profile"))
                throw new InvalidDataException("profile-variant");
            Call(global, "SetPreviewProfileScenario", value);
        }

        if (command.TryGetProperty("notice", out JsonElement notice))
        {
            string value = notice.GetString()!;
            if (value.Length > 2048) throw new InvalidDataException("notice-size-bound");
            if (GlobalView is { } global) Call(global, "ShowNotice", value);
            else ((TextBlock)Field(Window, "status")!).Text = value;
        }

        if (command.TryGetProperty("busy", out JsonElement busy))
        {
            object owner = GlobalView ?? Window;
            SetField(owner, "busy", busy.GetBoolean());
            if (GlobalView != null) Call(owner, "RefreshLayout");
            else Call(Window, "SetAccountControlsEnabled", !busy.GetBoolean());
        }
    }

    internal object Response(JsonElement command, Exception? rejected)
    {
        RequirePreview();
        object? capture = null;
        if (rejected == null && command.GetProperty("action").GetString() == "capture")
        {
            try
            {
                capture = UiSnapshotRenderer.Capture(this, command, directory, root);
            }
            catch (Exception error)
            {
                rejected = error;
            }
        }

        controls.Clear();
        FrameworkElement rootElement = (FrameworkElement)Window.Content;
        FrameworkElement card = (FrameworkElement)Field(Window, "card")!;
        FrameworkElement body = (FrameworkElement)Field(Window, "form")!;
        ScrollViewer scroll = (ScrollViewer)Field(Window, "scroll")!;
        List<object> nodes = [];
        Walk(rootElement, rootElement, nodes);
        DpiScale dpi = VisualTreeHelper.GetDpi(Window);
        Rect cardBounds = card.TransformToAncestor(rootElement)
            .TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
        revision++;
        object state = new
        {
            revision, region, language, syntheticOnly = true, authenticationRequests = 0,
            hostSha256 = HostSha256, previewTaskFaulted = preview.IsFaulted, windowVisible = Window.IsVisible,
            width = rootElement.ActualWidth, height = rootElement.ActualHeight,
            pixelWidth = Math.Round(rootElement.ActualWidth * dpi.DpiScaleX),
            pixelHeight = Math.Round(rootElement.ActualHeight * dpi.DpiScaleY),
            card = new
            {
                width = card.ActualWidth, height = card.ActualHeight,
                renderX = cardBounds.X, renderY = cardBounds.Y,
                renderWidth = cardBounds.Width, renderHeight = cardBounds.Height,
                bodyDesiredHeight = body.DesiredSize.Height, bodyActualHeight = body.ActualHeight,
                viewportHeight = scroll.ViewportHeight,
                scrollableHeight = scroll.ScrollableHeight,
                scrollbarVisible = scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible
            },
            mandatoryNotice = Field(Window, "noticeCompletion") != null,
            noticeConfirmation = Field(Window, "noticeConfirmation") is true,
            page = Field(Window, "helpOpen") is true ? "Help" :
                GlobalView is { } pageOwner ? Field(pageOwner, "page")?.ToString() :
                Field(Window, "localMode") is true ? "Saved" : "Sms",
            modeSelector = ModeSelectorState(rootElement),
            nodes, capture, promptProbe
        };
        string statePath = Path.Combine(directory, $"state-{revision:0000}.json");
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, Program.Json));
        return new { ok = rejected == null, error = rejected?.GetBaseException().Message, state, statePath };
    }

    private void Walk(DependencyObject value, FrameworkElement rootElement, List<object> nodes)
    {
        if (value is FrameworkElement element && !element.IsVisible) return;
        if (value is FrameworkElement current &&
            current is ButtonBase or TextBox or PasswordBox or Selector or TextBlock or ListBoxItem or Image)
        {
            string id = "e" + controls.Count;
            controls[id] = current;
            Point position = current.TransformToAncestor(rootElement).Transform(new Point());
            nodes.Add(new
            {
                id, role = Role(current), kind = current.GetType().Name, text = Label(current),
                name = AutomationProperties.GetName(current),
                automationId = AutomationProperties.GetAutomationId(current), enabled = current.IsEnabled,
                focused = current.IsKeyboardFocused,
                fontFamily = current is Control control ? control.FontFamily.Source :
                    current is TextBlock block ? block.FontFamily.Source : null,
                x = Math.Round(position.X, 2), y = Math.Round(position.Y, 2),
                width = Math.Round(current.ActualWidth, 2), height = Math.Round(current.ActualHeight, 2),
                @checked = current is ToggleButton toggle ? toggle.IsChecked : null,
                value = current is TextBox input && Role(current) is not ("secret" or "email-code") ? input.Text : null,
                secretCharacters = current is PasswordBox password ? password.Password.Length :
                    current is TextBox code && Role(current) is "secret" or "email-code" ? code.Text.Length :
                    (int?)null,
                selectedIndex = current is Selector selector ? selector.SelectedIndex : (int?)null,
                choices = current is Selector list ? list.Items.Cast<object>().Select(Label).ToArray() : null,
                selected = current is ListBoxItem item ? item.IsSelected : (bool?)null,
                asset = current is Image { Source: BitmapImage { UriSource: { IsFile: true } uri } }
                    ? Path.GetFileName(uri.LocalPath)
                    : null
            });
        }

        if (value is TextBlock text) WalkLinks(text.Inlines, text, rootElement, nodes);
        if (value is not Visual) return;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
            Walk(VisualTreeHelper.GetChild(value, i), rootElement, nodes);
    }

    private static object? ModeSelectorState(DependencyObject root)
    {
        if (root.GetType().Name == "SlidingModeSelector" && root is FrameworkElement { IsVisible: true })
        {
            TranslateTransform movement = (TranslateTransform)Field(root, "translation")!;
            Border thumb = (Border)Field(root, "thumb")!;
            return new { x = movement.X, width = thumb.ActualWidth, animated = movement.HasAnimatedProperties };
        }

        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (ModeSelectorState(VisualTreeHelper.GetChild(root, index)) is { } found)
                return found;
        return null;
    }

    private static string Label(object? value) => value switch
    {
        null => "", string text => text, TextBlock text => text.Text,
        PasswordBox => "", TextBox => "",
        ContentControl content => Label(content.Content),
        Panel panel => string.Join(" ", panel.Children.Cast<object>().Select(Label)),
        _ => value.ToString() ?? ""
    };

    private static string? Role(DependencyObject element)
    {
        string id = AutomationProperties.GetAutomationId(element);
        return id.Length > 0 ? id : element is ListBoxItem ? "saved-account" : null;
    }

    private void WalkLinks(InlineCollection inlines, TextBlock parent, FrameworkElement rootElement, List<object> nodes)
    {
        foreach (Inline inline in inlines)
        {
            if (inline is Hyperlink link)
            {
                string id = "e" + controls.Count;
                controls[id] = link;
                Rect bounds = link.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                bounds.Union(link.ContentEnd.GetCharacterRect(LogicalDirection.Backward));
                Point location = parent.TransformToAncestor(rootElement).Transform(new Point(bounds.X, bounds.Y));
                nodes.Add(new
                {
                    id, role = Role(link), kind = "Hyperlink",
                    text = new TextRange(link.ContentStart, link.ContentEnd).Text,
                    name = AutomationProperties.GetName(link),
                    automationId = AutomationProperties.GetAutomationId(link),
                    enabled = link.IsEnabled, focused = link.IsKeyboardFocused, x = location.X, y = location.Y,
                    width = bounds.Width, height = bounds.Height
                });
            }
            else if (inline is Span span) WalkLinks(span.Inlines, parent, rootElement, nodes);
        }
    }

    internal static object? Field(object target, string name) =>
        target.GetType().GetField(name, Members)?.GetValue(target);

    internal static void SetField(object target, string name, object? value) =>
        (target.GetType().GetField(name, Members) ?? throw new MissingFieldException(target.GetType().Name, name))
        .SetValue(target, value);

    internal static object? Call(object target, string method, params object?[] arguments) =>
        ((target as Type ?? target.GetType()).GetMethod(method, Members) ?? throw new MissingMethodException(method))
        .Invoke(target is Type ? null : target, arguments);

    internal void Close()
    {
        Call(Window, "Shutdown");
        string fixtureCommands = Path.Combine(directory, "fixture", "command.json");
        if (Directory.Exists(Path.GetDirectoryName(fixtureCommands)))
            File.WriteAllText(fixtureCommands, "{\"action\":\"close\"}");
    }

    public void Dispose()
    {
        if (Window.IsLoaded) Close();
    }
}
