using System;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

namespace TheShack.BlastFurnace
{
    // No Lossy Cooking Stations and ValheimCommunityPatch (both in The Shack Pack) each hand an item back when it reaches
    // a fermenter that is already busy, or a cooking station that is already full: the player's game took it out of the
    // inventory before the message got there. The game runs every mod's prefix, so with both on the player got two items
    // for one (two players loading the same fermenter at once). While VCP's "Refund Rejected Station Items" is on, No
    // Lossy's two hand-backs stand aside and VCP's does the job; the rest of No Lossy (its click limits, its smelter
    // hand-backs, which VCP's don't overlap) stays as it is.
    //
    // No Lossy's prefixes are patched like any other method. A prefix that skips one returns true for it, so the station's
    // own RPC still runs unless another prefix (VCP's) stops it. Hooked once, when the first ZNetScene loads, so it doesn't
    // matter which plugin loaded first.
    internal static class StationRefunds
    {
        private const string NoLossyGuid = "com.riintouge.nolossycookingstations";
        private const string VcpGuid = "MidnightsFX.ValheimCommunityPatch";
        private const string VcpRefundPatch = "ValheimCommunityPatch.Patches.Correctness.StationRefundPatch";

        private static bool hooked;
        private static ConfigEntry<bool> vcpRefund;
        private static bool vcpOnFermenter;
        private static bool vcpOnCooking;

        public static void Hook()
        {
            if (hooked)
            {
                return;
            }
            hooked = true;
            if (!Chainloader.PluginInfos.TryGetValue(NoLossyGuid, out var noLossy) || noLossy.Instance == null ||
                !Chainloader.PluginInfos.TryGetValue(VcpGuid, out var vcp) || vcp.Instance == null)
            {
                return;
            }
            try
            {
                vcp.Instance.Config.TryGetEntry("Fixes - Correctness", "Refund Rejected Station Items", out vcpRefund);
                vcpOnFermenter = HasVcpPrefix(AccessTools.Method(typeof(Fermenter), "RPC_AddItem"));
                vcpOnCooking = HasVcpPrefix(AccessTools.Method(typeof(CookingStation), "RPC_AddItem"));

                Assembly assembly = noLossy.Instance.GetType().Assembly;
                MethodInfo fermenter = NoLossyPrefix(assembly, "FermenterPatch");
                MethodInfo cooking = NoLossyPrefix(assembly, "CookingStationPatch");
                if (vcpRefund == null || fermenter == null || cooking == null)
                {
                    Plugin.Log.LogWarning("No Lossy Cooking Stations or ValheimCommunityPatch changed; a busy fermenter may still " +
                        "hand an item back twice.");
                    return;
                }
                Harmony harmony = new Harmony(Plugin.PluginGuid);
                harmony.Patch(fermenter, prefix: new HarmonyMethod(typeof(StationRefunds), nameof(FermenterPrefix)));
                harmony.Patch(cooking, prefix: new HarmonyMethod(typeof(StationRefunds), nameof(CookingPrefix)));
                Plugin.Log.LogInfo("No Lossy Cooking Stations' fermenter and cooking station hand-backs stand aside while " +
                    $"ValheimCommunityPatch's \"Refund Rejected Station Items\" is on (now {vcpRefund.Value}; VCP hooked on the " +
                    $"fermenter: {vcpOnFermenter}, cooking station: {vcpOnCooking}).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't hook No Lossy Cooking Stations; a busy fermenter may still hand an item back twice. {e}");
            }
        }

        // VCP turns its refunds off (doesn't patch) next to mods that fuel stations for free, so look for the patch itself.
        private static bool HasVcpPrefix(MethodBase method) =>
            method != null && (Harmony.GetPatchInfo(method)?.Prefixes.Any(p => p.PatchMethod.DeclaringType?.FullName == VcpRefundPatch) ?? false);

        private static MethodInfo NoLossyPrefix(Assembly assembly, string patchClass)
        {
            Type type = assembly.GetType($"NoLossyCookingStations.NoLossyCookingStations+{patchClass}");
            return type != null ? AccessTools.Method(type, "RPC_AddItemPrefix") : null;
        }

        private static bool VcpRefunds(bool hookedThere) => Settings.OneRefund.Value && hookedThere && vcpRefund.Value;

        // Returning false skips No Lossy's prefix; __result = true is its "let the station's RPC run".
        private static bool FermenterPrefix(ref bool __result)
        {
            if (!VcpRefunds(vcpOnFermenter))
            {
                return true;
            }
            __result = true;
            return false;
        }

        private static bool CookingPrefix(ref bool __result)
        {
            if (!VcpRefunds(vcpOnCooking))
            {
                return true;
            }
            __result = true;
            return false;
        }
    }
}
