using System.Xml.Linq;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Checks that both manual support entry points expose their icon and command.</summary>
public sealed class SupportEntryPointTests
{
    [Theory]
    [InlineData("MainWindow.xaml", "DonateButton", "{x:Bind ViewModel.DonateCommand}", "{x:Bind ViewModel.DonateTooltip, Mode=OneWay}")]
    [InlineData("SettingsPage.xaml", "SupportCard", "{x:Bind ViewModel.Info.SupportWinOptimizerCommand}", "{x:Bind ViewModel.Info.SupportLabel, Mode=OneWay}")]
    public void SupportActionHasHeartIconCommandAndAccessibleName(string file, string name, string command, string label)
    {
        using var stream = typeof(SupportEntryPointTests).Assembly.GetManifestResourceStream(file);
        var document = XDocument.Load(stream!);
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace icons = "using:FluentIcons.WinUI";
        var action = Assert.Single(document.Descendants(), element => (string?)element.Attribute(names + "Name") == name);

        Assert.Equal(command, (string?)action.Attribute("Command"));
        Assert.Equal(label, (string?)action.Attribute("AutomationProperties.Name"));
        Assert.Equal("Heart", (string?)Assert.Single(action.Descendants(icons + "FluentIcon")).Attribute("Icon"));
    }
}
