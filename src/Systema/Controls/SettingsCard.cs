// ════════════════════════════════════════════════════════════════════════════
// SettingsCard.cs  ·  One setting, laid out the way Windows 11 Settings does it
// ════════════════════════════════════════════════════════════════════════════
//
// Header (and an optional pill badge) with a description underneath on the left, and the
// control (Content) on the right. Mirrors the Windows Community Toolkit's SettingsCard so every
// page uses one identical row instead of a hand-built Grid per setting. The look lives in the
// implicit style in Resources/Themes/Dark.xaml.
//
//   <ctl:SettingsCard Header="Priority audio scheduling" Badge="Recommended"
//                     BadgeBrush="{StaticResource AccentGreenBrush}">
//       <ctl:SettingsCard.Description>
//           <TextBlock Style="{StaticResource CardDescription}">What it does…</TextBlock>
//       </ctl:SettingsCard.Description>
//       <CheckBox Style="{StaticResource ToggleSwitch}" IsChecked="{Binding BoostAudioScheduling}"/>
//   </ctl:SettingsCard>
//
// Description may be plain text or any content (several lines, a warning, a restart note), so
// nothing a page says has to be dropped to fit the layout. It is registered as a logical child,
// like Header and Content, so bindings and resource lookups inside it behave exactly as they did
// when the same XAML sat directly on the page.
//
// Ctrl+K search finds a setting by its header text: the header string becomes a TextBlock in the
// template, which MainWindow.FindVisibleText locates.
// ════════════════════════════════════════════════════════════════════════════

using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Systema.Controls;

public class SettingsCard : HeaderedContentControl
{
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(object), typeof(SettingsCard),
        new FrameworkPropertyMetadata(null, OnDescriptionChanged));

    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(
        nameof(Badge), typeof(string), typeof(SettingsCard), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty BadgeBrushProperty = DependencyProperty.Register(
        nameof(BadgeBrush), typeof(System.Windows.Media.Brush), typeof(SettingsCard), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty ExtraBadgeProperty = DependencyProperty.Register(
        nameof(ExtraBadge), typeof(string), typeof(SettingsCard), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty ExtraBadgeBrushProperty = DependencyProperty.Register(
        nameof(ExtraBadgeBrush), typeof(System.Windows.Media.Brush), typeof(SettingsCard), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty ExtraBadgeToolTipProperty = DependencyProperty.Register(
        nameof(ExtraBadgeToolTip), typeof(object), typeof(SettingsCard), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty RecommendKeyProperty = DependencyProperty.Register(
        nameof(RecommendKey), typeof(string), typeof(SettingsCard),
        new FrameworkPropertyMetadata(null, OnRecommendKeyChanged));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(SettingsCard),
        new FrameworkPropertyMetadata(new CornerRadius(6)));

    public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
        nameof(IconData), typeof(Geometry), typeof(SettingsCard), new FrameworkPropertyMetadata(null));

    /// <summary>Text or content under the header. Several lines are fine.</summary>
    public object? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Optional small pill beside the header, e.g. "Recommended" or "All devices".</summary>
    public string? Badge
    {
        get => (string?)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    /// <summary>Colour of the badge's text and outline.</summary>
    public System.Windows.Media.Brush? BadgeBrush
    {
        get => (System.Windows.Media.Brush?)GetValue(BadgeBrushProperty);
        set => SetValue(BadgeBrushProperty, value);
    }

    /// <summary>A second pill after Badge, for a card that carries two labels
    /// (e.g. a "Moderate" risk level plus "Recommended"). Green unless set.</summary>
    public string? ExtraBadge
    {
        get => (string?)GetValue(ExtraBadgeProperty);
        set => SetValue(ExtraBadgeProperty, value);
    }

    /// <summary>Colour of the second badge's text and outline.</summary>
    public System.Windows.Media.Brush? ExtraBadgeBrush
    {
        get => (System.Windows.Media.Brush?)GetValue(ExtraBadgeBrushProperty);
        set => SetValue(ExtraBadgeBrushProperty, value);
    }

    /// <summary>Hover text for the second badge, e.g. why this setting is recommended on this PC.</summary>
    public object? ExtraBadgeToolTip
    {
        get => GetValue(ExtraBadgeToolTipProperty);
        set => SetValue(ExtraBadgeToolTipProperty, value);
    }

    /// <summary>
    /// A key from <see cref="Systema.Core.RecommendationRules"/>. The green pill then shows what's
    /// recommended for THIS PC (or nothing), with the reason on hover. Replaces a hand-written
    /// "Recommended" so the page, Auto Pilot and Home can't disagree.
    /// </summary>
    public string? RecommendKey
    {
        get => (string?)GetValue(RecommendKeyProperty);
        set => SetValue(RecommendKeyProperty, value);
    }

    /// <summary>6 for a standalone card; 0 for a row inside an expander (ExpanderItem style).</summary>
    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Optional 24x24 line icon on the left, drawn with the theme's stroke.</summary>
    public Geometry? IconData
    {
        get => (Geometry?)GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    private static void OnRecommendKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (SettingsCard)d;
        if (e.NewValue is not string key || key.Length == 0)
        {
            BindingOperations.ClearBinding(card, ExtraBadgeProperty);
            BindingOperations.ClearBinding(card, ExtraBadgeToolTipProperty);
            return;
        }
        var source = Systema.Core.Recommend.Instance;
        BindingOperations.SetBinding(card, ExtraBadgeProperty,
            new System.Windows.Data.Binding($"[{key}]") { Source = source, Mode = BindingMode.OneWay });
        BindingOperations.SetBinding(card, ExtraBadgeToolTipProperty,
            new System.Windows.Data.Binding($"[{key}.Why]") { Source = source, Mode = BindingMode.OneWay });
    }

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (SettingsCard)d;
        if (e.OldValue != null) card.RemoveLogicalChild(e.OldValue);
        if (e.NewValue != null) card.AddLogicalChild(e.NewValue);
    }

    protected override IEnumerator LogicalChildren
    {
        get
        {
            var list = new ArrayList();
            var baseChildren = base.LogicalChildren;
            if (baseChildren != null)
                while (baseChildren.MoveNext()) list.Add(baseChildren.Current);
            if (Description != null) list.Add(Description);
            return list.GetEnumerator();
        }
    }
}

/// <summary>
/// Gives a plain-string SettingsCard description the CardDescription look. Anything else
/// (a TextBlock, a StackPanel of notes) gets null, which means "display it as it is".
/// </summary>
public sealed class StringDescriptionSelector : DataTemplateSelector
{
    public DataTemplate? StringTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is string ? StringTemplate : null;
}
