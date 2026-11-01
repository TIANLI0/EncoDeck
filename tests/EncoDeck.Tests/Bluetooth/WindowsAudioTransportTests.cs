// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.LeAudio;

namespace EncoDeck.Tests.Bluetooth;

public class WindowsAudioTransportTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string Original = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    [Fact]
    public void AccessEntry_GrantsStartStopAndQueryToOneUser()
    {
        // Windows reads the rights back as LCRPWP (sc sdshow after sc sdset, verified).
        Assert.Equal("(A;;LCRPWP;;;S-1-5-21-1-2-3-1001)", WindowsAudioTransport.AccessEntry(Sid));
    }

    [Fact]
    public void WithEntry_AppendsToTheDaclOnce()
    {
        string entry = WindowsAudioTransport.AccessEntry(Sid);
        string updated = WindowsAudioTransport.WithEntry(Original, entry);

        Assert.Equal(Original + entry, updated);
        Assert.Equal(updated, WindowsAudioTransport.WithEntry(updated, entry));
    }

    [Fact]
    public void WithEntry_KeepsTheSaclAfterTheDacl()
    {
        string entry = WindowsAudioTransport.AccessEntry(Sid);
        string withSacl = Original + "S:(AU;FA;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;WD)";

        Assert.Equal(Original + entry + "S:(AU;FA;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;WD)", WindowsAudioTransport.WithEntry(withSacl, entry));
    }
}
