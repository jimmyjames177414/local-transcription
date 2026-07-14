using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LocalTranscriber.App.Behaviors;

/// <summary>
/// Follow-live autoscroll for the transcript list. While following, content growth keeps the
/// view pinned to the bottom; a user scroll that leaves the bottom (with a 24px hysteresis
/// band) pauses following. Set <see cref="IsFollowingProperty"/> back to true (the "Following
/// live" chip click) to resume and jump to the end.
/// </summary>
public static class AutoScrollBehavior
{
    private const double BottomThresholdPx = 24;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(AutoScrollBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty IsFollowingProperty = DependencyProperty.RegisterAttached(
        "IsFollowing", typeof(bool), typeof(AutoScrollBehavior),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsFollowingChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);
    public static bool GetIsFollowing(DependencyObject obj) => (bool)obj.GetValue(IsFollowingProperty);
    public static void SetIsFollowing(DependencyObject obj, bool value) => obj.SetValue(IsFollowingProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement items)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            items.Loaded += OnItemsLoaded;
            if (items.IsLoaded)
            {
                Hook(items);
            }
        }
        else
        {
            items.Loaded -= OnItemsLoaded;
        }
    }

    private static void OnItemsLoaded(object sender, RoutedEventArgs e) => Hook((FrameworkElement)sender);

    private static void Hook(FrameworkElement items)
    {
        var viewer = FindScrollViewer(items);
        if (viewer is null)
        {
            return;
        }

        viewer.ScrollChanged -= OnScrollChanged;
        viewer.ScrollChanged += OnScrollChanged;

        void OnScrollChanged(object s, ScrollChangedEventArgs args)
        {
            bool following = GetIsFollowing(items);
            if (args.ExtentHeightChange != 0)
            {
                // Content grew (or virtualization re-estimated): keep pinned if following.
                if (following)
                {
                    viewer.ScrollToEnd();
                }
            }
            else if (args.VerticalChange != 0)
            {
                // Pure user scroll: follow iff we're within the bottom band.
                bool atBottom = viewer.VerticalOffset >= viewer.ExtentHeight - viewer.ViewportHeight - BottomThresholdPx;
                if (atBottom != following)
                {
                    SetIsFollowing(items, atBottom);
                }
            }
        }
    }

    private static void OnIsFollowingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && d is FrameworkElement items)
        {
            FindScrollViewer(items)?.ScrollToEnd();
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
