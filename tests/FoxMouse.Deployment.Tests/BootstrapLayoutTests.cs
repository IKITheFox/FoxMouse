using System.Drawing;
using System.Windows.Forms;
using FoxMouse.Bootstrap;

namespace FoxMouse.Deployment.Tests;

public sealed class BootstrapLayoutTests
{
    [Theory]
    [InlineData(0, 620)]
    [InlineData(1, 620)]
    [InlineData(0, 540)]
    [InlineData(1, 540)]
    public void LanguageAndActionsStayWithinSinglePage(int language, int width)
    {
        StaTestThread.Run(() =>
        {
            using BootstrapWindow window = new();
            window.Show();
            window.ClientSize = new Size(width, 460);
            var layout = Assert.IsType<TableLayoutPanel>(window.Controls[0]);
            var picker = Assert.IsAssignableFrom<ComboBox>(layout.GetControlFromPosition(0, 2));
            picker.SelectedIndex = language;
            window.PerformLayout();
            window.Update();
            Assert.Equal(SizeType.Percent, layout.ColumnStyles[0].SizeType);
            foreach (Control control in layout.Controls)
            {
                Assert.True(layout.ClientRectangle.Contains(control.Bounds), $"Outside page: {control.GetType().Name} {control.Bounds}");
            }
            var folderPanel = Assert.IsType<TableLayoutPanel>(layout.GetControlFromPosition(0, 3));
            Assert.Equal(3, folderPanel.Controls.Count);
            foreach (Control control in folderPanel.Controls)
                Assert.True(folderPanel.ClientRectangle.Contains(control.Bounds));
            var actions = Assert.IsType<FlowLayoutPanel>(layout.GetControlFromPosition(0, 5));
            Assert.Equal(2, actions.Controls.Cast<Control>().Count(button => button.Visible));
            // The log action is intentionally hidden until an error. Verify
            // the most crowded state too, rather than requiring it at startup.
            foreach (Control button in actions.Controls) button.Visible = true;
            var progressPanel = Assert.IsType<Panel>(layout.GetControlFromPosition(0, 4));
            var progress = Assert.Single(progressPanel.Controls.OfType<ProgressBar>());
            progress.Visible = true;
            window.PerformLayout();
            progressPanel.PerformLayout();
            Assert.True(progressPanel.ClientRectangle.Contains(progress.Bounds));
            var status = Assert.Single(progressPanel.Controls.OfType<Label>());
            Assert.False(status.Bounds.IntersectsWith(progress.Bounds));
            foreach (Control button in actions.Controls)
            {
                Assert.True(button.Visible);
                Assert.True(actions.ClientRectangle.Contains(button.Bounds), $"Action clipped: {button.Text} {button.Bounds}");
            }
            Assert.False(window.AutoScroll);
        });
    }
}
