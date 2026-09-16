using System.Xml.Linq;

namespace FoxMouse.Settings.Tests;

public sealed class ExclusionsPageContractTests
{
    [Fact]
    public void RecreatedSearchBoxDisplaysTheRetainedViewModelQuery()
    {
        // This is a source contract, not a substitute for real navigation QA.
        // Without the source-to-control binding, a recreated page displays an
        // empty search box while the shared model still filters the app list.
        XDocument page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ExclusionsPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement search = Assert.Single(page.Descendants(), element =>
            (string?)element.Attribute(x + "Name") == "ProcessSearchBox");
        Assert.Equal("{Binding ProcessSearch, Mode=OneWay}", (string?)search.Attribute("Text"));
        Assert.Equal("ProcessSearchBox_TextChanged", (string?)search.Attribute("TextChanged"));
    }
}
