// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Brands.Akg;

namespace EncoDeck.Protocol.Brands;

/// <summary>
/// A BLE GATT service that carries a brand's ordinary byte stream: commands are written to one
/// characteristic and replies arrive as notifications on another. The named characteristic is used
/// when the service has it; otherwise (or when the UUID is null) the first writable / first notifying
/// characteristic of the service.
/// </summary>
/// <param name="NameMatchedLeFallback">
/// When the classic address has no such service, the endpoint may be found on one separate LE
/// advertiser named like the paired device (or "&lt;name&gt;-LE"); never used when several match.
/// </param>
/// <param name="BondedLeIdentities">
/// LE Audio earbuds carry the service on each earbud's bonded LE identity: the connected identities
/// are tried first, so one earbud worn alone is reached even when it is not the one at the public address.
/// </param>
/// <param name="PreferUnacknowledgedWrites">
/// Write without response whenever the write characteristic allows it, even if it also declares
/// acknowledged writes. For firmware that never answers ATT write requests on its control characteristic.
/// </param>
public sealed record GattStreamEndpoint(Guid Service, Guid? Write, Guid? Notify, string Name, bool NameMatchedLeFallback = false, bool BondedLeIdentities = false,
    bool PreferUnacknowledgedWrites = false);

/// <summary>
/// Byte-stream GATT services opened by the shared notify/write channel. Sony's MDR GATT service is
/// not listed: it is opened by the dedicated LE Audio channel (keep-alive, LE-Audio-only detection)
/// for both the LE Audio link and the fallback after the RFCOMM services.
/// </summary>
public static class GattStreamEndpoints
{
    public static IReadOnlyList<GattStreamEndpoint> All { get; } =
    [
        new(AkgBleUuids.BesService, AkgBleUuids.BesWrite, AkgBleUuids.BesNotify, "AKG BES GATT", NameMatchedLeFallback: true),
        new(AkgBleUuids.RaceService, AkgBleUuids.RaceWrite, AkgBleUuids.RaceNotify, "Airoha RACE GATT", NameMatchedLeFallback: true),
        new(Xiaomi.XiaomiProtocol.MmaGattServiceUuid, Xiaomi.XiaomiProtocol.MmaGattWriteUuid, Xiaomi.XiaomiProtocol.MmaGattNotifyUuid, "Xiaomi MMA GATT", BondedLeIdentities: true,
            PreferUnacknowledgedWrites: true),
    ];

    public static GattStreamEndpoint? Find(Guid service) => All.FirstOrDefault(e => e.Service == service);
}
