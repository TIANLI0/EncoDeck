// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.A2dpCodec;

namespace EncoDeck.Tests.Platform;

public sealed class A2dpSinkDriverTests
{
    [Theory]
    [InlineData(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&000102B0_PID&0000\7&2D2F1C3A&0&E498E03A2AA3_C00000000", 0xE498E03A2AA3UL)]
    [InlineData(@"bthenum\{0000110b-0000-1000-8000-00805f9b34fb}_LOCALMFG&0002\7&1A2B3C4D&0&2475b3bfaa96_C00000000", 0x2475B3BFAA96UL)]
    public void AudioSinkNode_NamesItsDevice(string instance, ulong address) =>
        Assert.Equal<ulong?>(address, A2dpSinkDriver.Address(instance));

    [Theory]
    // Hands-Free, the device node itself, a malformed address, no instance part.
    [InlineData(@"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_VID&000102B0_PID&0000\7&2D2F1C3A&0&E498E03A2AA3_C00000000")]
    [InlineData(@"BTHENUM\Dev_E498E03A2AA3\8&2D2F1C3A&0&BluetoothDevice_E498E03A2AA3")]
    [InlineData(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&000102B0_PID&0000\7&2D2F1C3A&0&E498E03A2AZZ_C00000000")]
    [InlineData(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}")]
    public void OtherNodes_NameNoDevice(string instance) =>
        Assert.Null(A2dpSinkDriver.Address(instance));
}
