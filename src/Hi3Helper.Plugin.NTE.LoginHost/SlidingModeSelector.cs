using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Nte.LoginHost;

internal sealed class SlidingModeSelector : Border
{
    private readonly Grid track = new();
    private readonly Border thumb = new() { CornerRadius = new(8), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TranslateTransform translation = new();
    private readonly RadioButton first, second;

    internal SlidingModeSelector(RadioButton first, RadioButton second)
    {
        this.first = first;
        this.second = second;
        Padding = new(3);
        CornerRadius = new(10);
        SetResourceReference(BackgroundProperty, "InputBrush");
        thumb.SetResourceReference(BackgroundProperty, "AccentBrush");
        thumb.RenderTransform = translation;
        track.Children.Add(thumb);
        Grid labels = new();
        labels.ColumnDefinitions.Add(new());
        labels.ColumnDefinitions.Add(new());
        labels.Children.Add(first);
        Grid.SetColumn(second, 1);
        labels.Children.Add(second);
        track.Children.Add(labels);
        Child = track;
        SizeChanged += (_, _) => Move(false);
        first.Checked += (_, _) => Move(true);
        second.Checked += (_, _) => Move(true);
    }

    private void Move(bool animate)
    {
        double width = track.ActualWidth / 2;
        if (width <= 0) return;
        thumb.Width = width;
        double target = second.IsChecked == true ? width : 0;
        if (animate && IsLoaded && SystemParameters.ClientAreaAnimation)
            translation.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            translation.BeginAnimation(TranslateTransform.XProperty, null);
            translation.X = target;
        }
    }
}
