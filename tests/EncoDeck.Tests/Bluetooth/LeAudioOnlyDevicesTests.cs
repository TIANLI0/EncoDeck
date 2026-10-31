// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.LeAudio;

namespace EncoDeck.Tests.Bluetooth;

public sealed class LeAudioOnlyDevicesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "encodeck-tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "le-audio-devices.json");

    public LeAudioOnlyDevicesTests() => LeAudioOnlyDevices.Configure(FilePath);

    public void Dispose()
    {
        LeAudioOnlyDevices.Configure(Path.Combine(_directory, "unused.json"));
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Record_PersistsAndFindsByIdOrAddress()
    {
        var device = new LeAudioOnlyDevice(0xA1B2C3D4E5F6, "OPPO Enco X4", "Bluetooth#Bluetootha0:00:00:00:00:01-a1:b2:c3:d4:e5:f6");
        LeAudioOnlyDevices.Record(device);

        // A fresh load reads the file, not the cached list.
        LeAudioOnlyDevices.Configure(FilePath);
        Assert.Equal(device, LeAudioOnlyDevices.Find(0xA1B2C3D4E5F6));
        Assert.Equal(device, LeAudioOnlyDevices.Find(device.DeviceId.ToUpperInvariant()));
        Assert.Null(LeAudioOnlyDevices.Find(0x010203040506));
    }

    [Fact]
    public void Record_ReplacesTheEntryOfTheSameAddress()
    {
        LeAudioOnlyDevices.Record(new LeAudioOnlyDevice(0x0000AA, "Old", "id-old"));
        LeAudioOnlyDevices.Record(new LeAudioOnlyDevice(0x0000AA, "New", "id-new"));

        var all = LeAudioOnlyDevices.All;
        Assert.Single(all);
        Assert.Equal("id-new", all[0].DeviceId);
    }

    [Fact]
    public void Remove_ForgetsTheDevice()
    {
        int changes = 0;
        void OnChanged(object? sender, EventArgs e) => changes++;
        LeAudioOnlyDevices.Changed += OnChanged;
        try
        {
            LeAudioOnlyDevices.Record(new LeAudioOnlyDevice(0x0000BB, "X3", "id-x3"));
            LeAudioOnlyDevices.Remove(0x0000BB);
            LeAudioOnlyDevices.Remove(0x0000BB);
        }
        finally { LeAudioOnlyDevices.Changed -= OnChanged; }

        Assert.Empty(LeAudioOnlyDevices.All);
        LeAudioOnlyDevices.Configure(FilePath);
        Assert.Empty(LeAudioOnlyDevices.All);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void CorruptFile_IsTreatedAsEmpty()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");
        LeAudioOnlyDevices.Configure(FilePath);

        Assert.Empty(LeAudioOnlyDevices.All);
    }
}
