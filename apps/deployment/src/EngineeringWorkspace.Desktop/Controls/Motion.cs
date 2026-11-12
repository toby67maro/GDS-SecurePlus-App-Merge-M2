using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace EngineeringWorkspace.Desktop.Controls;

/// <summary>
/// Motion used across the application: pages glide in when they open, tiles and chart cards lift on hover, and
/// elements can fade/slide in when loaded. Respects the Windows "show animations" setting.
/// </summary>
public static class Motion
{
    private static bool Enabled => SystemParameters.ClientAreaAnimation;

    // ---- Entrance when a ContentControl's content changes (the page host). ----
    public static readonly DependencyProperty EntranceOnContentChangeProperty = DependencyProperty.RegisterAttached(
        "EntranceOnContentChange", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEntranceOnContentChanged));

    public static bool GetEntranceOnContentChange(DependencyObject element) => (bool)element.GetValue(EntranceOnContentChangeProperty);
    public static void SetEntranceOnContentChange(DependencyObject element, bool value) => element.SetValue(EntranceOnContentChangeProperty, value);

    private static void OnEntranceOnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl control || e.NewValue is not true) return;
        DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl))
            .AddValueChanged(control, (_, _) => Enter(control, 16, 0.32));
    }

    // ---- Fade / slide in once when loaded. ----
    public static readonly DependencyProperty FadeInProperty = DependencyProperty.RegisterAttached(
        "FadeIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeInChanged));

    public static bool GetFadeIn(DependencyObject element) => (bool)element.GetValue(FadeInProperty);
    public static void SetFadeIn(DependencyObject element, bool value) => element.SetValue(FadeInProperty, value);

    private static void OnFadeInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && e.NewValue is true) element.Loaded += (_, _) => Enter(element, 10, 0.28);
    }

    /// <summary>Plays the entrance: from transparent and <paramref name="offset"/> pixels lower to its place.</summary>
    public static void Enter(UIElement element, double offset, double seconds)
    {
        if (!Enabled) return;
        var translate = EnsureTranslate(element);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromSeconds(seconds);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, 0, duration) { EasingFunction = ease });
    }

    // ---- Lift on hover (tiles, chart cards, gallery items). ----
    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached(
        "HoverLift", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnHoverLiftChanged));

    public static bool GetHoverLift(DependencyObject element) => (bool)element.GetValue(HoverLiftProperty);
    public static void SetHoverLift(DependencyObject element, bool value) => element.SetValue(HoverLiftProperty, value);

    private static void OnHoverLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (e.NewValue is true)
        {
            element.MouseEnter += OnLiftEnter;
            element.MouseLeave += OnLiftLeave;
        }
        else
        {
            element.MouseEnter -= OnLiftEnter;
            element.MouseLeave -= OnLiftLeave;
        }
    }

    private static void OnLiftEnter(object sender, System.Windows.Input.MouseEventArgs e) => SafeLift((UIElement)sender, true);
    private static void OnLiftLeave(object sender, System.Windows.Input.MouseEventArgs e) => SafeLift((UIElement)sender, false);

    /// <summary>Hover motion is decoration: if an element cannot be animated it simply does not lift, and no error is shown.</summary>
    private static void SafeLift(UIElement element, bool up)
    {
        try
        {
            Lift(element, up);
        }
        catch (InvalidOperationException)
        {
            element.MouseEnter -= OnLiftEnter;
            element.MouseLeave -= OnLiftLeave;
        }
    }

    /// <summary>The opacity each lifted shadow returns to when the mouse leaves.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DropShadowEffect, object> BaseOpacityStore = new();

    private static class BaseOpacity
    {
        public static void AddOrUpdate(DropShadowEffect shadow, double value) => BaseOpacityStore.AddOrUpdate(shadow, value);

        public static bool TryGetValue(DropShadowEffect shadow, out double value)
        {
            var found = BaseOpacityStore.TryGetValue(shadow, out var boxed);
            value = found ? (double)boxed! : 0;
            return found;
        }
    }

    private static void Lift(UIElement element, bool up)
    {
        var translate = EnsureTranslate(element);
        var duration = TimeSpan.FromSeconds(Enabled ? 0.16 : 0);
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(up ? -4 : 0, duration) { EasingFunction = new QuadraticEase() });
        if (element.Effect is not DropShadowEffect shadow)
        {
            shadow = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0, Color = Colors.Black };
            element.Effect = shadow;
            BaseOpacity.AddOrUpdate(shadow, 0);
        }
        else if (shadow.IsFrozen)
        {
            // A shadow from a shared style or theme resource is frozen and cannot be animated: use this element's own copy,
            // and return to the style's own opacity when the mouse leaves.
            var original = shadow.Opacity;
            shadow = shadow.Clone();
            element.Effect = shadow;
            BaseOpacity.AddOrUpdate(shadow, original);
        }

        var rest = BaseOpacity.TryGetValue(shadow, out var stored) ? stored : shadow.Opacity;
        BaseOpacity.AddOrUpdate(shadow, rest);
        shadow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(up ? Math.Max(rest, 0.28) : rest, duration));
    }

    private static TranslateTransform EnsureTranslate(UIElement element)
    {
        switch (element.RenderTransform)
        {
            case TranslateTransform existing when !existing.IsFrozen:
                return existing;
            case TransformGroup group when group.Children.OfType<TranslateTransform>().FirstOrDefault() is { IsFrozen: false } inGroup:
                return inGroup;
        }

        var translate = new TranslateTransform();
        if (element.RenderTransform is null || element.RenderTransform == Transform.Identity) element.RenderTransform = translate;
        else element.RenderTransform = new TransformGroup { Children = { element.RenderTransform, translate } };
        return translate;
    }
}
