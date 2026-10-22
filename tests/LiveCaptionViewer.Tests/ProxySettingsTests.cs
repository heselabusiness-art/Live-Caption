using LiveCaptionViewer.Models;

namespace LiveCaptionViewer.Tests;

public class ProxySettingsTests
{
    [Fact]
    public void Disabled_proxy_leaves_the_browser_arguments_alone()
    {
        var s = new ProxySettings { Enabled = false, Host = "10.0.0.1", Port = 8080 };

        Assert.Equal("--x", s.AppendToBrowserArguments("--x"));
    }

    [Theory]
    [InlineData("", 8080)]
    [InlineData("   ", 8080)]
    [InlineData("10.0.0.1", 0)]
    public void Incomplete_proxy_is_ignored(string host, int port)
    {
        var s = new ProxySettings { Enabled = true, Host = host, Port = port };

        Assert.Equal("--x", s.AppendToBrowserArguments("--x"));
    }

    [Fact]
    public void Enabled_proxy_adds_server_and_bypass_list()
    {
        var s = new ProxySettings { Enabled = true, Host = " proxy.local ", Port = 3128, Scheme = "socks5", BypassList = "localhost" };

        Assert.Equal(
            "--x --proxy-server=socks5://proxy.local:3128 --proxy-bypass-list=localhost",
            s.AppendToBrowserArguments("--x"));
    }
}
