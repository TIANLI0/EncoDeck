// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Profiles;
using EncoDeck.Protocol.Brands.Edifier;
using EncoDeck.Protocol.Brands.Huawei;
using EncoDeck.Protocol.Brands.Honor;
using EncoDeck.Protocol.Brands.Moondrop;
using EncoDeck.Protocol.Brands.Vivo;
using EncoDeck.Protocol.Brands.Xiaomi;
using EncoDeck.Protocol.Oppo.Models;

namespace EncoDeck.Protocol.Brands;

/// <summary>Every model profile EncoDeck ships, across all brand protocols.</summary>
public static class BuiltInProfiles
{
    public static IReadOnlyList<IDeviceProfile> Create() =>
    [
        .. OppoDeviceProfile.CreateBuiltIn(),
        .. EdifierProfiles.CreateBuiltIn(),
        .. XiaomiProfiles.CreateBuiltIn(),
        .. VivoProfiles.CreateBuiltIn(),
        .. HuaweiProfiles.CreateBuiltIn(),
        .. HonorProfiles.CreateBuiltIn(),
        .. EarFun.EarFunProfiles.CreateBuiltIn(),
        .. MoondropProfiles.CreateBuiltIn(),
        .. Sony.SonyAkgProfiles.CreateBuiltIn(),
        .. Sennheiser.SennheiserProfiles.Create(),
        .. Samsung.SamsungProfiles.CreateBuiltIn(),
        .. Google.GoogleProfiles.CreateBuiltIn(),
        .. Bose.BoseProfiles.CreateBuiltIn(),
        .. Nothing.NothingProfiles.CreateBuiltIn(),
        .. Usb.UsbHeadsetProfiles.Create(),
    ];
}
