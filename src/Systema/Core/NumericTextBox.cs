using System.Windows;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;
using DataObject = System.Windows.DataObject;

namespace Systema.Core;

/// <summary>
/// Attached property that restricts a <see cref="TextBox"/> to digits only.
/// Set <c>core:NumericTextBox.IsNumericOnly="True"</c> in XAML.
///
/// Without this, the numeric setting boxes (Game Booster check interval, Task
/// Sleep CPU caps) accept any text. Typing a letter caused a silent binding
/// failure to the int property — the box kept a stale value with no feedback.
/// This rejects non-digit keystrokes and non-numeric pastes up front so the
/// bound value can never go invalid.
///
/// Implementation note: deliberately uses a plain char scan instead of a
/// compiled <c>Regex</c>. <c>RegexOptions.Compiled</c> emits IL at runtime
/// (Reflection.Emit), which Smart App Control / Defender treat as a suspicious
/// behavioural pattern for an UNSIGNED binary. A digit check needs no regex.
/// </summary>
public static class NumericTextBox
{
    public static readonly DependencyProperty IsNumericOnlyProperty =
        DependencyProperty.RegisterAttached(
            "IsNumericOnly", typeof(bool), typeof(NumericTextBox),
            new PropertyMetadata(false, OnIsNumericOnlyChanged));

    public static bool GetIsNumericOnly(DependencyObject obj) => (bool)obj.GetValue(IsNumericOnlyProperty);
    public static void SetIsNumericOnly(DependencyObject obj, bool value) => obj.SetValue(IsNumericOnlyProperty, value);

    private static bool HasNonDigit(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (char c in s)
            if (c < '0' || c > '9') return true;
        return false;
    }

    private static void OnIsNumericOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        if ((bool)e.NewValue)
        {
            box.PreviewTextInput += OnPreviewTextInput;
            DataObject.AddPastingHandler(box, OnPaste);
        }
        else
        {
            box.PreviewTextInput -= OnPreviewTextInput;
            DataObject.RemovePastingHandler(box, OnPaste);
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = HasNonDigit(e.Text);

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(typeof(string)) is string text && HasNonDigit(text))
            e.CancelCommand();
        else if (!e.DataObject.GetDataPresent(typeof(string)))
            e.CancelCommand();
    }
}
