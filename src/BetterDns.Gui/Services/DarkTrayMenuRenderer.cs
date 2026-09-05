using System.Drawing;
using System.Windows.Forms;

namespace BetterDns.Gui.Services;

internal sealed class DarkTrayMenuRenderer : ToolStripProfessionalRenderer
{
    internal static readonly Color Background = Color.FromArgb(32, 32, 32);
    internal static readonly Color Foreground = Color.FromArgb(244, 244, 244);
    private static readonly Color Highlight = Color.FromArgb(56, 56, 56);
    private static readonly Color Border = Color.FromArgb(69, 69, 69);

    internal DarkTrayMenuRenderer() : base(new DarkColors()) { RoundedEdges = true; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Foreground : Color.FromArgb(155, 155, 155);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Border);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 8, y, Math.Max(8, e.Item.Width - 8), y);
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public DarkColors() { UseSystemColors = false; }
        public override Color ToolStripDropDownBackground => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Highlight;
        public override Color MenuItemSelected => Highlight;
        public override Color MenuItemSelectedGradientBegin => Highlight;
        public override Color MenuItemSelectedGradientEnd => Highlight;
        public override Color MenuItemPressedGradientBegin => Highlight;
        public override Color MenuItemPressedGradientMiddle => Highlight;
        public override Color MenuItemPressedGradientEnd => Highlight;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
    }
}
