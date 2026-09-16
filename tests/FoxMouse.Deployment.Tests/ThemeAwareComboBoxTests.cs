using System.Drawing;
using System.Windows.Forms;
using FoxMouse.Presentation;

namespace FoxMouse.Deployment.Tests;

public sealed class ThemeAwareComboBoxTests
{
    [Theory]
    [InlineData(44, 245)]
    [InlineData(255, 27)]
    [InlineData(0, 255)]
    public void ClosedFieldRendersTheChosenThemeSurface(int background, int foreground)
    {
        StaTestThread.Run(() =>
        {
            using Form form = new();
            using ThemeAwareComboBox picker = new()
            {
                Width = 220,
                BackColor = Color.FromArgb(background, background, background),
                ForeColor = Color.FromArgb(foreground, foreground, foreground),
            };
            form.Controls.Add(picker);
            picker.Items.AddRange(["简体中文", "English"]);
            picker.SelectedIndex = 1;
            form.Show();
            form.Update();
            picker.Update();
            using Bitmap bitmap = new(picker.Width, picker.Height);
            picker.DrawToBitmap(bitmap, picker.ClientRectangle);
            Assert.Equal(picker.BackColor.ToArgb(), bitmap.GetPixel(150, picker.Height / 2).ToArgb());
            Assert.Equal(picker.BackColor.ToArgb(), bitmap.GetPixel(picker.Width - 5, 4).ToArgb());
            Assert.Equal(ComboBoxStyle.DropDownList, picker.DropDownStyle);
            Assert.Equal(DrawMode.OwnerDrawFixed, picker.DrawMode);
            Assert.Equal("English", picker.SelectedItem);
            picker.SelectedIndex = 0;
            Assert.Equal("简体中文", picker.SelectedItem);
        });
    }
}
