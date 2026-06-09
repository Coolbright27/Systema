using System.Windows.Controls;

namespace Systema.Views;

/// <summary>
/// Pure-vector app backdrop (gradient base + radial glows + faint grid, rings,
/// scan lines, constellation dots and Fluent card outlines). Rendered natively
/// in XAML — no image asset — so it scales crisply at any window size and adds
/// no files for Smart App Control / Defender to scrutinise. Translated from the
/// Assets/systema_bg_v2.html canvas prototype at a 1480×833 reference size.
/// </summary>
public partial class BackdropLayer : UserControl
{
    public BackdropLayer() => InitializeComponent();
}
