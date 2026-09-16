using System.Drawing;
using System.Windows.Forms;

namespace FoxMouse.Presentation
{
    public static class InstallerChrome
    {
        public static void Apply(System.IntPtr handle, bool dark, Color background, Color foreground)
        {
            if (handle == System.IntPtr.Zero) return;
            try
            {
                int mode = dark && !SystemInformation.HighContrast ? 1 : 0;
                Set(handle, 20, mode);
                Set(handle, 33, 2);
                Set(handle, 34, SystemInformation.HighContrast ? -1 : -2);
                Set(handle, 35, SystemInformation.HighContrast ? -1 : background.R | background.G << 8 | background.B << 16);
                Set(handle, 36, SystemInformation.HighContrast ? -1 : foreground.R | foreground.G << 8 | foreground.B << 16);
            }
            catch (System.DllNotFoundException) { }
            catch (System.EntryPointNotFoundException) { }
        }
        private static void Set(System.IntPtr handle, uint attribute, int value)
        {
            DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        }
        [System.Runtime.InteropServices.DllImport("dwmapi.dll", ExactSpelling = true)]
        private static extern int DwmSetWindowAttribute(System.IntPtr handle, uint attribute, ref int value, int size);
    }

    public sealed class InstallerProgressBar : ProgressBar
    {
        private readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer();
        private int offset;
        public InstallerProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer, true);
            animation.Interval = 40;
            animation.Tick += delegate { offset = (offset + 6) % System.Math.Max(1, Width); Invalidate(); };
        }
        protected override void OnVisibleChanged(System.EventArgs e)
        {
            base.OnVisibleChanged(e);
            animation.Enabled = Visible;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            int width = Style == ProgressBarStyle.Marquee ? System.Math.Max(20, Width / 4) :
                (int)(Width * (Value - Minimum) / (double)System.Math.Max(1, Maximum - Minimum));
            using (var brush = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(246, 107, 43)))
            {
                e.Graphics.FillRectangle(brush, Style == ProgressBarStyle.Marquee ? offset - width : 0, 0, width, Height);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) animation.Dispose();
            base.Dispose(disposing);
        }
    }

    // Native Button semantics (keyboard, accessibility, DialogResult), with a
    // borderless renderer shared by the dependency-free bootstrapper.
    public sealed class InstallerButton : Button
    {
        public InstallerButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? SystemColors.Window : Parent.BackColor);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int d = System.Math.Min(16, System.Math.Min(r.Width, r.Height));
            if (d < 1) return;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                using (var brush = new SolidBrush(BackColor)) e.Graphics.FillPath(brush, path);
                if (SystemInformation.HighContrast)
                    using (var pen = new Pen(SystemColors.WindowFrame)) e.Graphics.DrawPath(pen, path);
            }
            Color ink = Enabled ? ForeColor : SystemColors.GrayText;
            TextRenderer.DrawText(e.Graphics, Text, Font, r, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(r, -4, -4), ink, BackColor);
        }
    }

    // Shared with the inbox-.NET-Framework bootstrapper. Keep C# 5 compatible.
    public sealed class ThemeAwareComboBox : ComboBox
    {
        public ThemeAwareComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            // OwnerDraw covers items, not the native arrow button. Paint the
            // closed field after native painting while retaining native input,
            // dropdown, selection and accessibility behavior.
            if (message.Msg == 0x000F && IsHandleCreated)
            {
                using (Graphics graphics = Graphics.FromHwnd(Handle)) DrawClosedField(graphics);
            }
            else if ((message.Msg == 0x0317 || message.Msg == 0x0318) && message.WParam != System.IntPtr.Zero)
            {
                using (Graphics graphics = Graphics.FromHdc(message.WParam)) DrawClosedField(graphics);
            }
        }

        private void DrawClosedField(Graphics graphics)
        {
            Rectangle bounds = ClientRectangle;
            if (bounds.Width < 4 || bounds.Height < 4) return;
            Color textColor = Enabled ? ForeColor : SystemColors.GrayText;
            using (SolidBrush background = new SolidBrush(BackColor)) graphics.FillRectangle(background, bounds);
            Color borderColor = SystemInformation.HighContrast ? SystemColors.WindowFrame : textColor;
            if (SystemInformation.HighContrast)
                using (Pen border = new Pen(borderColor)) graphics.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
            int arrowWidth = bounds.Height;
            Rectangle textBounds = new Rectangle(6, 1, System.Math.Max(0, bounds.Width - arrowWidth - 8), bounds.Height - 2);
            TextRenderer.DrawText(graphics, Text, Font, textBounds, textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            float centerX = bounds.Width - arrowWidth / 2F;
            float centerY = bounds.Height / 2F;
            float half = System.Math.Max(3F, bounds.Height / 8F);
            using (Pen arrow = new Pen(textColor, System.Math.Max(1F, bounds.Height / 24F)))
                graphics.DrawLines(arrow, new PointF[] {
                    new PointF(centerX - half, centerY - half / 2F),
                    new PointF(centerX, centerY + half / 2F),
                    new PointF(centerX + half, centerY - half / 2F) });
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(textBounds, -1, -2), textColor, BackColor);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            // A closed, focused field should retain the active theme surface.
            bool highlighted = selected && DroppedDown;
            Color background = highlighted ? SystemColors.Highlight : BackColor;
            Color foreground = !Enabled ? SystemColors.GrayText :
                highlighted ? SystemColors.HighlightText : ForeColor;
            using (SolidBrush brush = new SolidBrush(background))
                e.Graphics.FillRectangle(brush, e.Bounds);
            string label = (e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text) ?? string.Empty;
            Rectangle textBounds = Rectangle.Inflate(e.Bounds, -4, 0);
            TextRenderer.DrawText(e.Graphics, label, Font, textBounds, foreground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) != 0)
                ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, foreground, background);
            base.OnDrawItem(e);
        }
    }
}
