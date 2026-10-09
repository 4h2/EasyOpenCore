using EasyOpenCore.Core.Plist;

namespace EasyOpenCore.Core.Tests;

public class PlistTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
        	<key>Zeta</key>
        	<string>a &amp; b</string>
        	<key>Alpha</key>
        	<array>
        		<integer>42</integer>
        		<true/>
        		<data>
        		AAEC
        		</data>
        	</array>
        	<key>Empty</key>
        	<dict/>
        </dict>
        </plist>
        """;

    [Fact]
    public void ParsesAllTypesAndKeepsKeyOrder()
    {
        var root = PlistSerializer.Parse(Sample).AsDict;

        Assert.Equal(["Zeta", "Alpha", "Empty"], root.Keys);
        Assert.Equal("a & b", root.GetString("Zeta"));
        var arr = root["Alpha"].AsArray.Items;
        Assert.Equal(42, ((PInteger)arr[0]).Value);
        Assert.True(((PBool)arr[1]).Value);
        Assert.Equal(new byte[] { 0, 1, 2 }, ((PData)arr[2]).Value);
        Assert.Equal(0, root["Empty"].AsDict.Count);
    }

    [Fact]
    public void RoundTripsWithoutChanges()
    {
        var xml = PlistSerializer.ToXml(PlistSerializer.Parse(Sample));
        Assert.Equal(xml, PlistSerializer.ToXml(PlistSerializer.Parse(xml)));
        Assert.Contains("\t<key>Zeta</key>\n\t<string>a &amp; b</string>", xml);
        Assert.Contains("<data>AAEC</data>", xml);
    }
}
