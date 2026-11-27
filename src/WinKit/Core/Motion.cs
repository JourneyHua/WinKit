using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinKit.Core;

/// <summary>Short, interruptible render-only cues. Logical state and hit targets never wait for a clock.</summary>
public static class Motion
{
    private const int PressMilliseconds = 65;
    private const int FeedbackMilliseconds = 110;
    private const int ContextMilliseconds = 150;
    private static readonly QuadraticEase EaseOut = CreateEase();
    private static readonly Dictionary<(DependencyObject Target, DependencyProperty Property), AnimationClock> Active = new();
    private static bool _suppressForVerification;

    static Motion() => SystemParameters.StaticPropertyChanged += OnSystemSettingsChanged;

    internal static bool IsEnabled => !_suppressForVerification && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    internal static int ActiveCount => Active.Count;

    // For exercising the same zero-motion path even when the test desktop has animations enabled.
    internal static bool SuppressForVerification
    {
        get => _suppressForVerification;
        set { _suppressForVerification = value; if (value) ResetActive(); }
    }

    public static readonly DependencyProperty FeedbackBrushProperty = DependencyProperty.RegisterAttached(
        "FeedbackBrush", typeof(Brush), typeof(Motion), new PropertyMetadata(Brushes.Black));
    public static Brush GetFeedbackBrush(DependencyObject element) => (Brush)element.GetValue(FeedbackBrushProperty);
    public static void SetFeedbackBrush(DependencyObject element, Brush value) => element.SetValue(FeedbackBrushProperty, value);

    // For dedicated, non-hit-test overlay layers that start at Opacity=0; not for the entire control.
    public static readonly DependencyProperty LevelProperty = DependencyProperty.RegisterAttached(
        "Level", typeof(double), typeof(Motion), new PropertyMetadata(0d, OnLevelChanged));
    public static double GetLevel(DependencyObject element) => (double)element.GetValue(LevelProperty);
    public static void SetLevel(DependencyObject element, double value) => element.SetValue(LevelProperty, value);

    public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached(
        "Reveal", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnRevealChanged));
    public static bool GetReveal(DependencyObject element) => (bool)element.GetValue(RevealProperty);
    public static void SetReveal(DependencyObject element, bool value) => element.SetValue(RevealProperty, value);

    // A change of identity on a small, persistent visual (e.g. the page heading), never on the whole page.
    public static readonly DependencyProperty ContextProperty = DependencyProperty.RegisterAttached(
        "Context", typeof(object), typeof(Motion), new PropertyMetadata(null, OnContextChanged));
    public static object? GetContext(DependencyObject element) => element.GetValue(ContextProperty);
    public static void SetContext(DependencyObject element, object? value) => element.SetValue(ContextProperty, value);

    public static readonly DependencyProperty AngleProperty = DependencyProperty.RegisterAttached(
        "Angle", typeof(double), typeof(Motion), new PropertyMetadata(0d, OnAngleChanged));
    public static double GetAngle(DependencyObject element) => (double)element.GetValue(AngleProperty);
    public static void SetAngle(DependencyObject element, double value) => element.SetValue(AngleProperty, value);

    public static readonly DependencyProperty ProgressPercentProperty = DependencyProperty.RegisterAttached(
        "ProgressPercent", typeof(double), typeof(Motion), new PropertyMetadata(0d, OnProgressChanged));
    public static double GetProgressPercent(DependencyObject element) => (double)element.GetValue(ProgressPercentProperty);
    public static void SetProgressPercent(DependencyObject element, double value) => element.SetValue(ProgressPercentProperty, value);

    public static readonly DependencyProperty IsPopupSurfaceProperty = DependencyProperty.RegisterAttached(
        "IsPopupSurface", typeof(bool), typeof(Motion), new PropertyMetadata(false));
    public static bool GetIsPopupSurface(DependencyObject element) => (bool)element.GetValue(IsPopupSurfaceProperty);
    public static void SetIsPopupSurface(DependencyObject element, bool value) => element.SetValue(IsPopupSurfaceProperty, value);

    private static void OnProgressChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        if (element.RenderTransform is not ScaleTransform { IsFrozen: false } fill)
        {
            fill = new ScaleTransform((double)args.OldValue / 100, 1);
            element.RenderTransform = fill;
        }
        var value = Math.Clamp((double)args.NewValue / 100, 0, 1);
        SetAnimatedBase(element, fill, ScaleTransform.ScaleXProperty, value, FeedbackMilliseconds);
    }

    private static void OnLevelChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        var level = Math.Clamp((double)args.NewValue, 0, 1);
        SetAnimatedBase(element, element, UIElement.OpacityProperty, level,
            level >= 0.12 && level < 0.3 ? PressMilliseconds : FeedbackMilliseconds);
    }

    private static void OnAngleChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        if (element.RenderTransform is not RotateTransform { IsFrozen: false } rotation)
        {
            rotation = new RotateTransform((double)args.OldValue);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = rotation;
        }
        SetAnimatedBase(element, rotation, RotateTransform.AngleProperty, (double)args.NewValue, ContextMilliseconds);
    }

    private static void OnRevealChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        if ((bool)args.NewValue)
        {
            element.Loaded += OnRevealLoaded;
            element.IsVisibleChanged += OnVisibilityChanged;
        }
        else
        {
            element.Loaded -= OnRevealLoaded;
            element.IsVisibleChanged -= OnVisibilityChanged;
            Reset(element, UIElement.OpacityProperty);
        }
    }

    private static void OnRevealLoaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        // The first IsVisibleChanged can arrive before the popup or new view is loaded.
        if (!Active.ContainsKey((element, UIElement.OpacityProperty))) RevealNow(element);
    }

    private static void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if ((bool)args.NewValue) RevealNow(element);
        else Reset(element, UIElement.OpacityProperty);
    }

    private static void OnContextChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is FrameworkElement element && args.OldValue is not null && args.NewValue is not null)
            RevealNow(element);
    }

    private static void RevealNow(FrameworkElement element)
    {
        if (!CanAnimate(element) || (GetIsPopupSurface(element) && !SystemParameters.MenuAnimation)) return;
        var key = (element, UIElement.OpacityProperty);
        var end = (double)element.GetAnimationBaseValue(UIElement.OpacityProperty);
        var start = Active.ContainsKey(key) ? element.Opacity : end * 0.76;
        Animate(element, UIElement.OpacityProperty, start, end, ContextMilliseconds);
    }

    private static bool CanAnimate(FrameworkElement owner) => owner.IsLoaded && owner.IsVisible && IsEnabled;

    private static void SetAnimatedBase(FrameworkElement owner, DependencyObject target, DependencyProperty property, double value, int milliseconds)
    {
        var previous = (double)target.GetValue(property);
        // These properties belong to the motion layer, not to a binding. Store a real base value
        // so a template's initial value cannot return when WPF removes or replaces the clock.
        target.SetValue(property, value);
        if (CanAnimate(owner)) Animate(target, property, previous, value, milliseconds);
        else Reset(target, property);
    }

    private static void Animate(DependencyObject target, DependencyProperty property, double start, double end, int milliseconds)
    {
        if (Math.Abs(start - end) < 0.001)
        {
            Reset(target, property);
            return;
        }

        var key = (target, property);
        var clock = new DoubleAnimation(start, end, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop
        }.CreateClock();
        Active[key] = clock;
        clock.Completed += (_, _) =>
        {
            // A second interaction replaced this clock; it must not be cleared by an old completion.
            if (Active.TryGetValue(key, out var current) && ReferenceEquals(clock, current)) Reset(target, property);
        };
        ApplyClock(target, property, clock);
    }

    private static void Reset(DependencyObject target, DependencyProperty property)
    {
        if (Active.Remove((target, property))) ApplyClock(target, property, null);
    }

    private static void ApplyClock(DependencyObject target, DependencyProperty property, AnimationClock? clock)
    {
        if (target is UIElement element) element.ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace);
        else if (target is Animatable value) value.ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnSystemSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast)
            or nameof(SystemParameters.MenuAnimation) or null or "")) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        void SettleIfSuppressed()
        {
            if (!IsEnabled) ResetActive();
            else if (!SystemParameters.MenuAnimation) ResetActive(popupOnly: true);
        }
        if (dispatcher.CheckAccess()) SettleIfSuppressed();
        else _ = dispatcher.InvokeAsync(SettleIfSuppressed);
    }

    private static void ResetActive(bool popupOnly = false)
    {
        foreach (var key in Active.Keys.ToArray())
            if (!popupOnly || GetIsPopupSurface(key.Target)) Reset(key.Target, key.Property);
    }

    private static QuadraticEase CreateEase()
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }
}
