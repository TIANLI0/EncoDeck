// SPDX-License-Identifier: GPL-3.0-only
// EarKit a77acd18890a5f28746c2303bfff0e23fff8d8ca (MIT), model-specific key layouts and function values.
using EncoDeck.Core.State;
using EncoDeck.Core.Capabilities;

namespace EncoDeck.Protocol.Brands.EarFun;

public static class EarFunGestures
{
    public static ushort Read(EarFunModel model) => model.Gaia ? (ushort)0x030B : (ushort)0x2070;
    public static ushort Write(EarFunModel model) => model.Gaia ? (ushort)0x030A : (ushort)0x2075;
    public static IReadOnlyList<BrandGestureSlot> Slots(EarFunModel model)
    {
        if (!model.Has("keys")) return [];
        int[] functions = model.Gaia ? [1,2,3,4,5,6,7,8,9,10,11,13,16]
            : model.Variant == "tw230" ? [0,1,2,3,4,5,6,7,8,9,10,11,12,14,15]
            : model.Variant is "cw100" or "cw200" ? [0,1,2,3,4,5,6,7,8,9,10,11,12,13]
            : [0,1,2,3,4,5,6,7,8,9,10,11,12];
        DeviceCapability? Requires(int value) => value switch
        {
            7 => DeviceCapability.GamingMode,
            8 or 9 => DeviceCapability.NoiseCancellation,
            10 or 11 when model.Gaia => DeviceCapability.NoiseCancellation,
            14 or 15 when model.Variant == "tw230" => DeviceCapability.NoiseCancellation,
            12 when !model.Gaia && model.Variant is not ("tw230" or "cw100" or "cw200") => DeviceCapability.DualDeviceConnection,
            _ => null,
        };
        BrandGestureChoice[] choices = [.. functions.Where(v => Requires(v) switch
            { DeviceCapability.NoiseCancellation => model.Has("noise"), DeviceCapability.DualDeviceConnection => model.Has("dual"), _ => true })
            .Select(v => new BrandGestureChoice(v, ["EarFun_" + (model.Gaia ? "Q" : model.Variant == "tw230" ? "T" : model.Variant is "cw100" or "cw200" ? "C" : "E") + "Action" + v], Requires(v)))];
        var slots = new List<BrandGestureSlot>();
        void Add(EarSide side, int button, GestureAction action, string label) => slots.Add(new(side, button, action, label, choices));
        if (model.Headphones)
        {
            foreach (int button in new[] { 0,1,2 })
            {
                Add(EarSide.Both, button, GestureAction.SingleSqueeze, "EarFun_Button" + button + "Tap");
                Add(EarSide.Both, button, GestureAction.SqueezeAndHold, "EarFun_Button" + button + "Hold");
            }
        }
        else if (model.Gaia)
        {
            for (int gesture = 1; gesture <= 4; gesture++)
                foreach (var side in new[] { EarSide.Left, EarSide.Right }) Add(side, 0, (GestureAction)gesture, gesture == 4 ? "Gesture_TapHold" : "Gesture_Tap" + gesture);
        }
        else
        {
            foreach (var side in new[] { EarSide.Left, EarSide.Right })
                for (int gesture = 1; gesture <= 4; gesture++) Add(side, 0, (GestureAction)gesture, gesture == 4 ? "Gesture_TapHold" : "Gesture_Tap" + gesture);
            if (model.Variant == "cw200")
                foreach (var side in new[] { EarSide.Left, EarSide.Right })
                {
                    Add(side, KeyFunction.CallButton, GestureAction.TripleSqueeze, "EarFun_CallTriple");
                    Add(side, KeyFunction.CallButton, GestureAction.SqueezeAndHold, "EarFun_CallHold");
                }
        }
        return slots;
    }
    public static IReadOnlyList<KeyFunction>? Decode(EarFunModel model, byte[] payload)
    {
        var slots = Slots(model);
        if (payload.Length != slots.Count * (model.Gaia ? 2 : 1)) return null;
        if (model.Gaia)
            for (int i = 0; i < slots.Count; i++)
                if (payload[2 * i] != (slots[i].Action == GestureAction.SqueezeAndHold ? 17 : (int)slots[i].Action)) return null;
        return [.. slots.Select((s, i) => new KeyFunction(s.Side, s.Button, s.Action, payload[model.Gaia ? 2 * i + 1 : i]))];
    }
}
