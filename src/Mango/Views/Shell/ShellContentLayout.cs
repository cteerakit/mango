using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Mango.Views.Shell;

internal static class ShellContentLayout
{
    public const double MinWidth = 560;
    public const double MaxWidth = 720;
    public const double HorizontalPadding = 64;

    /// <summary>
    /// Keep shell pages on a shared column width: grow/shrink between min and max.
    /// Pass <see cref="double.PositiveInfinity"/> to use the full viewport width.
    /// </summary>
    public static void Attach(FrameworkElement viewport, FrameworkElement column, double maxWidth = MaxWidth)
    {
        column.PointerWheelChanged += OnColumnPointerWheelChanged;

        void Update()
        {
            var available = viewport.ActualWidth - HorizontalPadding;
            if (available <= 0)
            {
                return;
            }

            column.Width = Math.Clamp(available, MinWidth, maxWidth);
        }

        viewport.SizeChanged += (_, _) => Update();
        column.Loaded += (_, _) => Update();
    }

    /// <summary>
    /// Wide tables: fill the viewport when content is narrow, but allow the column to
    /// grow with content so a parent <see cref="ScrollViewer"/> can scroll horizontally.
    /// </summary>
    public static void AttachExpanding(FrameworkElement viewport, FrameworkElement column)
    {
        column.PointerWheelChanged += OnColumnPointerWheelChanged;

        void Update()
        {
            var available = viewport.ActualWidth - HorizontalPadding;
            if (available <= 0)
            {
                return;
            }

            column.ClearValue(FrameworkElement.WidthProperty);
            column.MinWidth = Math.Max(MinWidth, available);
        }

        viewport.SizeChanged += (_, _) => Update();
        column.Loaded += (_, _) => Update();
    }

    private static void OnColumnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (e.KeyModifiers != VirtualKeyModifiers.Shift)
        {
            return;
        }

        var column = (FrameworkElement)sender;
        var properties = e.GetCurrentPoint(column).Properties;
        if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0)
        {
            return;
        }

        // Handle on the content before the wheel reaches the ScrollViewer's vertical scrolling.
        for (var parent = VisualTreeHelper.GetParent(column); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer viewer)
            {
                continue;
            }

            if (viewer.ScrollableWidth > 0)
            {
                var offset = Math.Clamp(viewer.HorizontalOffset - properties.MouseWheelDelta, 0, viewer.ScrollableWidth);
                viewer.ChangeView(offset, null, null, disableAnimation: true);
                e.Handled = true;
            }

            return;
        }
    }
}
