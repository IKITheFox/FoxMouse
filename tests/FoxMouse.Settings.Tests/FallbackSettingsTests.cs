using FoxMouse.App;
using FoxMouse.Core;
using System.Drawing;
using System.Windows.Forms;

namespace FoxMouse.Settings.Tests;

public sealed class FallbackSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnglishFallbackShowsActionsAndPreservesEdits(bool saveChanges)
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            string previousLanguage = UiText.Language;
            try
            {
                UiText.Configure("en-US");
                using SettingsForm form = new(FoxMouseSettings.Default with { Language = "en-US" });
                form.Shown += (_, _) =>
                {
                    try
                    {
                        form.Update();
                        Control[] controls = Descendants(form).ToArray();
                        var save = controls.OfType<Button>().Single(button => button.DialogResult == DialogResult.OK);
                        var cancel = controls.OfType<Button>().Single(button => button.DialogResult == DialogResult.Cancel);
                        Assert.Equal(UiText.Get("FallbackSave", "en-US"), save.Text);
                        Assert.Equal(UiText.Get("Cancel", "en-US"), cancel.Text);
                        Assert.True(save.Visible && cancel.Visible);
                        Assert.True(form.RectangleToScreen(form.ClientRectangle).Contains(save.RectangleToScreen(save.ClientRectangle)));
                        Assert.True(form.RectangleToScreen(form.ClientRectangle).Contains(cancel.RectangleToScreen(cancel.ClientRectangle)));
                        var enabled = controls.OfType<CheckBox>().Single(control => control.Text == UiText.Get("FallbackEnable", "en-US"));
                        enabled.Checked = !enabled.Checked;
                        Assert.True(save.Enabled);
                        Assert.Equal(!FoxMouseSettings.Default.Enabled, form.Result.Enabled);
                        Assert.Equal("en-US", form.Result.Language);
                        string evidence = Path.Combine(Path.GetTempPath(), "FoxMouse.FallbackLayoutTests", Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(evidence);
                        using Bitmap bitmap = new(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(evidence, "english.png"));
                        Console.WriteLine("Fallback layout evidence: " + evidence);
                        (saveChanges ? save : cancel).PerformClick();
                    }
                    catch (Exception exception) { error = exception; form.Close(); }
                };
                Assert.Equal(saveChanges ? DialogResult.OK : DialogResult.Cancel, form.ShowDialog());
                Assert.False(form.Visible);
            }
            catch (Exception exception) { error = exception; }
            finally { UiText.Configure(previousLanguage); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void SavingFallbackSettingsPreservesLanguage(string language)
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            try
            {
                using SettingsForm form = new(FoxMouseSettings.Default with { Language = language });
                Assert.Equal(language, form.Result.Language);
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
