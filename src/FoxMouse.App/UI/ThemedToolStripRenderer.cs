namespace FoxMouse.App.UI;

internal sealed class ThemedToolStripRenderer : ToolStripProfessionalRenderer
{
    public ThemedToolStripRenderer(SemanticColors colors)
        : base(new FoxMouseColorTable(colors))
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        SemanticColors colors = ((FoxMouseColorTable)ColorTable).Colors;
        e.TextColor = !e.Item.Enabled
            ? colors.SecondaryText
            : e.Item.Selected
                ? colors.SelectionText
                : colors.Text;
        base.OnRenderItemText(e);
    }

    private sealed class FoxMouseColorTable : ProfessionalColorTable
    {
        internal FoxMouseColorTable(SemanticColors colors)
        {
            Colors = colors;
            UseSystemColors = colors.IsHighContrast;
        }

        internal SemanticColors Colors { get; }

        public override Color ToolStripDropDownBackground => Colors.Surface;

        public override Color ImageMarginGradientBegin => Colors.Surface;

        public override Color ImageMarginGradientMiddle => Colors.Surface;

        public override Color ImageMarginGradientEnd => Colors.Surface;

        public override Color MenuBorder => Colors.Border;

        public override Color MenuItemBorder => Colors.Selection;

        public override Color MenuItemSelected => Colors.Selection;

        public override Color MenuItemSelectedGradientBegin => Colors.Selection;

        public override Color MenuItemSelectedGradientEnd => Colors.Selection;

        public override Color MenuItemPressedGradientBegin => Colors.Card;

        public override Color MenuItemPressedGradientMiddle => Colors.Card;

        public override Color MenuItemPressedGradientEnd => Colors.Card;

        public override Color CheckBackground => Colors.Card;

        public override Color CheckPressedBackground => Colors.Selection;

        public override Color CheckSelectedBackground => Colors.Selection;

        public override Color SeparatorDark => Colors.Border;

        public override Color SeparatorLight => Colors.Surface;
    }
}
