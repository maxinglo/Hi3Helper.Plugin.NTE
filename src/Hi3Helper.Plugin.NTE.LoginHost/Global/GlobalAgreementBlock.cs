using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Nte.LoginHost.Localization;
using Nte.Login;

namespace Nte.LoginHost;

internal sealed class GlobalAgreementBlock : Grid
{
    internal GlobalAgreementBlock(CheckBox checkbox, Style style, NteOverseasRegion? region = null)
    {
        Margin = new(0, 6, 0, 0);
        ColumnDefinitions.Add(new() { Width = new(24) });
        ColumnDefinitions.Add(new());
        checkbox.Style = style;
        checkbox.Content = null;
        checkbox.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(checkbox, LoginText.Get("Agreement"));
        AutomationProperties.SetAutomationId(checkbox, "agreement");
        Children.Add(checkbox);
        TextBlock text = new()
            { FontSize = 12, LineHeight = 20, TextWrapping = TextWrapping.Wrap, Margin = new(8, 4, 0, 0) };
        string format = LoginText.Get("AgreementInline");
        while (format.Length > 0)
        {
            int terms = format.IndexOf("{0}", StringComparison.Ordinal),
                privacy = format.IndexOf("{1}", StringComparison.Ordinal);
            int position = terms < 0 ? privacy : privacy < 0 ? terms : Math.Min(terms, privacy);
            if (position < 0)
            {
                text.Inlines.Add(new Run(format));
                break;
            }

            if (position > 0) text.Inlines.Add(new Run(format[..position]));
            bool isTerms = position == terms;
            Hyperlink link = new(new Run(LoginText.Get(isTerms ? "TermsName" : "PrivacyName")))
            {
                NavigateUri = isTerms ? (region ?? NteOverseasRegion.Global).TermsUri : (region ?? NteOverseasRegion.Global).PrivacyUri
            };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            AutomationProperties.SetAutomationId(link, isTerms ? "terms" : "privacy");
            link.RequestNavigate += (_, args) =>
            {
                Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
                args.Handled = true;
            };
            text.Inlines.Add(link);
            format = format[(position + 3)..];
        }

        text.MouseLeftButtonUp += (_, args) =>
        {
            if (args.OriginalSource is not Run { Parent: Hyperlink } && args.OriginalSource is not Hyperlink)
                checkbox.IsChecked = checkbox.IsChecked != true;
        };
        Grid.SetColumn(text, 1);
        Children.Add(text);
    }
}
