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

    /// <summary>Optional 24x24 line icon on the left, drawn with the theme's stroke.</summary>
    public Geometry? IconData
    {
        get => (Geometry?)GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
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
