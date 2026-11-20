using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using Nte.Login;
using Nte.LoginHost.Localization;

namespace Nte.LoginHost;

internal sealed class OfficialLauncherShortcutPrompt : Window
{
    private readonly CheckBox startMenu = CreateChoice("OfficialShortcutStartMenu", "shortcut-start-menu");
    private readonly CheckBox desktop = CreateChoice("OfficialShortcutDesktop", "shortcut-desktop");
    private readonly TextBlock status = new()
    {
        IsVisible = false, Foreground = new SolidColorBrush(Color.FromRgb(255, 196, 165)),
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Button proceed = new() { MinHeight = 42, IsDefault = true };
    private readonly Action<Environment.SpecialFolder> writeShortcut;

    internal OfficialLauncherShortcutPrompt(Action<Environment.SpecialFolder> writeShortcut)
    {
        this.writeShortcut = writeShortcut;
        Title = LoginText.Get("OfficialShortcutTitle");
        Width = 480;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        Foreground = new SolidColorBrush(Color.FromRgb(232, 241, 247));

        proceed.Content = LoginText.Get("OfficialShortcutContinue");
        proceed.SetResourceReference(ThemeProperty, "AccentButtonStyle");
        proceed.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetAutomationId(proceed, "shortcut-continue");
        proceed.Click += (_, _) => ApplySelection();

        StackPanel choices = new() { Spacing = 2 };
        choices.Children.Add(startMenu);
        choices.Children.Add(desktop);
        Grid header = new() { ColumnDefinitions = new("*,Auto") };
        TextBlock heading = new() { Text = Title, FontSize = 20, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        heading.PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(heading).Properties.IsLeftButtonPressed) BeginMoveDrag(args);
        };
        header.Children.Add(heading);
        Button close = new() { Content = "×", FontSize = 20, Width = 34, Height = 34, Margin = new(8, 0, 0, 0) };
        close.SetResourceReference(ThemeProperty, "QuietButtonStyle");
        AutomationProperties.SetName(close, LoginText.Get("Close"));
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        StackPanel layout = new() { Spacing = 14 };
        layout.Children.Add(header);
        layout.Children.Add(new TextBlock
        {
            Text = LoginText.Get("OfficialShortcutDescription"), TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(choices);
        layout.Children.Add(status);
        layout.Children.Add(proceed);
        Content = new Border
        {
            Child = layout, Padding = new(22), CornerRadius = new(16), BorderThickness = new(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(115, 129, 171, 189)),
            Background = new SolidColorBrush(Color.FromRgb(31, 45, 64))
        };
    }

    internal static async Task ShowAsync(NteOfficialLaunchPlan plan, NteHostRegion region)
    {
        OfficialLauncherShortcutPrompt prompt = new(folder => OfficialLauncherShortcutPromptWriter.Create(plan, region, folder));
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        prompt.Closed += (_, _) => closed.TrySetResult();
        prompt.Show();
        await closed.Task;
    }

    private static CheckBox CreateChoice(string key, string id)
    {
        CheckBox option = new() { IsChecked = false, Content = LoginText.Get(key) };
        option.SetResourceReference(ThemeProperty, "FormCheckBoxStyle");
        AutomationProperties.SetAutomationId(option, id);
        return option;
    }

    private void ApplySelection()
    {
        if (status.IsVisible)
        {
            Close();
            return;
        }

        List<string> failed = [];
        TryCreate(startMenu, Environment.SpecialFolder.Programs, failed);
        TryCreate(desktop, Environment.SpecialFolder.DesktopDirectory, failed);
        if (failed.Count == 0)
        {
            Close();
            return;
        }

        startMenu.IsEnabled = desktop.IsEnabled = false;
        status.Text = LoginText.Get("OfficialShortcutFailed") + Environment.NewLine + string.Join(Environment.NewLine, failed);
        status.IsVisible = true;
        proceed.Focus();
    }

    private void TryCreate(CheckBox choice, Environment.SpecialFolder folder, List<string> failed)
    {
        if (choice.IsChecked != true) return;
        try
        {
            writeShortcut(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
                                      System.Runtime.InteropServices.ExternalException)
        {
            failed.Add((string)choice.Content!);
            NteHostStartupDiagnostics.Phase("OfficialShortcutFailed", error);
        }
    }
}
