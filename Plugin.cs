using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace TheShack.BlastFurnace
{
    // The blast furnace also takes every ore the regular furnace takes (copper ore, tin ore, scrap iron, silver ore,
    // copper scrap, scrap bronze, and any a game update or another mod adds to it) and smelts them faster than the
    // regular furnace does. Black metal, flametal and the blast furnace's other ores stay exactly as they were.
    //
    //   - What a furnace takes is its m_conversion list, which every furnace copies from its prefab when it loads. The
    //     mod adds the regular furnace's own conversions for those ores to each blast furnace as it loads, and again to
    //     the loaded ones whenever a setting changes (also when the server's settings arrive). The game checks that list
    //     on the PC of the player adding ore and on the PC that runs the furnace, which is why everyone needs the mod
    //     (Jotunn makes sure). The list of ores is read from the regular furnace itself, so nothing is left out.
    //   - A furnace smelts one ore every m_secPerProduct seconds and burns m_fuelPerProduct coal over that time, the same
    //     for everything it takes. UpdateSmelter (run by whoever's game looks after the furnace) reads those two fields;
    //     the mod swaps them for calls that look at the ore at the front of the queue: the regular furnace's ores get the
    //     speed and coal from the settings, everything else keeps the furnace's own numbers.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    [SynchronizationMode(AdminOnlyStrictness.IfOnServer)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "theshack.blastfurnace";
        public const string PluginName = "Shack Blast Furnace Ascended";
        public const string PluginVersion = "1.1.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Settings.Bind(Config);

            // A mod that throws in Awake takes the chainloader down with it. If the game changed under us, say so and
            // leave the blast furnace as the game has it.
            if (!Furnaces.Ready)
            {
                Log.LogError($"{PluginName} can't find Smelter.GetQueuedOre in this game version; the blast furnace stays as the game has it.");
                return;
            }
            Harmony harmony = new Harmony(PluginGuid);
            try
            {
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception e)
            {
                harmony.UnpatchSelf();
                Log.LogError($"{PluginName} could not hook into this game version; the blast furnace stays as the game has it. {e}");
                return;
            }
            Config.SettingChanged += (_, __) => Furnaces.ApplyToLoaded();
            SynchronizationManager.OnConfigurationSynchronized += (_, __) => Furnaces.ApplyToLoaded();
            Log.LogInfo($"{PluginName} {PluginVersion} ready ({Settings.Speed.Value:0.##}x speed, {Settings.Coal.Value:0.##}x coal).");
        }
    }

    internal static class Settings
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> Speed;
        public static ConfigEntry<float> Coal;
        public static ConfigEntry<int> OreCapacity;
        public static ConfigEntry<int> CoalCapacity;
        public static ConfigEntry<bool> OneRefund;

        // The regular furnace's ores with a setting of their own, by the item's prefab name. Any other ore it takes (the
        // game's unused iron ore, or one a game update or another mod adds) goes by OtherOres.
        public static readonly Dictionary<string, ConfigEntry<bool>> Ores = new Dictionary<string, ConfigEntry<bool>>();
        public static ConfigEntry<bool> OtherOres;

        // Whether the blast furnace takes this one of the regular furnace's ores.
        public static bool Ticked(string ore) =>
            Ores.TryGetValue(ore, out ConfigEntry<bool> entry) ? entry.Value : OtherOres.Value;

        public static void Bind(ConfigFile config)
        {
            int order = 100;
            ConfigEntry<T> Bind<T>(string section, string key, T value, string description, AcceptableValueBase range = null) =>
                config.Bind(section, key, value, new ConfigDescription(description, range,
                    new ConfigurationManagerAttributes { IsAdminOnly = true, Order = order-- }));

            Enabled = Bind("1 - General", "Enabled", true,
                "The blast furnace takes the ores ticked under \"2 - Ores\" and smelts them at the speed below. Off: the blast " +
                "furnace works as in the base game (any of those ores already in it still finish).");
            Speed = Bind("1 - General", "Speed", 4f,
                "How many times faster than the regular furnace the blast furnace smelts those ores. 4 = four times as fast; " +
                "1 = as slow as the regular furnace.",
                new AcceptableValueRange<float>(1f, 20f));
            Coal = Bind("1 - General", "Coal per ore", 1f,
                "Coal those ores burn, compared with the regular furnace. 1 = the same coal per ore (it just burns faster), " +
                "0.5 = half, 0 = none (it still needs some coal in it to run).",
                new AcceptableValueRange<float>(0f, 5f));
            OreCapacity = Bind("1 - General", "Ore capacity", 0,
                "How much ore the blast furnace holds, all kinds together (black metal and flametal too). 0 = the game's own.",
                new AcceptableValueRange<int>(0, 100));
            CoalCapacity = Bind("1 - General", "Coal capacity", 0,
                "How much coal the blast furnace holds. 0 = the game's own.",
                new AcceptableValueRange<int>(0, 200));

            Ores["CopperOre"] = Bind("2 - Ores", "Copper ore", true, "The blast furnace smelts copper ore into copper.");
            Ores["TinOre"] = Bind("2 - Ores", "Tin ore", true, "The blast furnace smelts tin ore into tin.");
            Ores["IronScrap"] = Bind("2 - Ores", "Scrap iron", true, "The blast furnace smelts scrap iron into iron.");
            Ores["SilverOre"] = Bind("2 - Ores", "Silver ore", true, "The blast furnace smelts silver ore into silver.");
            Ores["CopperScrap"] = Bind("2 - Ores", "Copper scrap", true, "The blast furnace smelts copper scrap into copper.");
            Ores["BronzeScrap"] = Bind("2 - Ores", "Scrap bronze", true, "The blast furnace smelts scrap bronze into bronze.");
            OtherOres = Bind("2 - Ores", "Other ores", true,
                "Any other ore the regular furnace takes: the game's unused iron ore, and any ore a game update or another " +
                "mod adds to the regular furnace. The blast furnace smelts it into what the regular furnace makes.");

            OneRefund = Bind("3 - Compatibility", "One refund per rejected item", true,
                "With No Lossy Cooking Stations and ValheimCommunityPatch both installed, an item that reaches a busy " +
                "fermenter or a full cooking station is handed back once, by VCP (\"Refund Rejected Station Items\"). " +
                "Off: No Lossy hands it back too, so a busy fermenter gives two. Does nothing without both mods, or while " +
                "VCP's refund is off.");
        }
    }

    internal static class Furnaces
    {
        private const string BlastFurnaceName = "$piece_blastfurnace";

        private static readonly MethodInfo GetQueuedOre = AccessTools.Method(typeof(Smelter), "GetQueuedOre");
        private static readonly Func<Smelter, string> QueuedOre =
            GetQueuedOre != null ? AccessTools.MethodDelegate<Func<Smelter, string>>(GetQueuedOre) : null;

        // False if the game no longer has what the mod reads; the plugin then doesn't patch anything.
        public static bool Ready => QueuedOre != null;

        // The prefabs, which nothing changes: the regular furnace's ores, speed and coal, and the blast furnace's own capacity.
        private static Smelter regular;
        private static Smelter blast;

        public static bool IsBlastFurnace(Smelter smelter) => smelter.m_name == BlastFurnaceName;

        private static Smelter Regular() => Prefab(ref regular, "smelter");

        private static Smelter Blast() => Prefab(ref blast, "blastfurnace");

        private static Smelter Prefab(ref Smelter cache, string name)
        {
            if (cache == null && ZNetScene.instance != null)
            {
                GameObject prefab = ZNetScene.instance.GetPrefab(name);
                cache = prefab != null ? prefab.GetComponent<Smelter>() : null;
            }
            return cache;
        }

        // The regular furnace's conversion for this ore, if it's one of the regular furnace's ores (one the blast furnace
        // doesn't take by itself), whether it's ticked or not; otherwise null.
        public static Smelter.ItemConversion RegularConversion(string ore)
        {
            Smelter smelter = Regular(), blastPrefab = Blast();
            if (smelter == null || blastPrefab == null || string.IsNullOrEmpty(ore) || Find(blastPrefab, ore) != null)
            {
                return null;
            }
            return Find(smelter, ore);
        }

        // The first conversion of this ore in the furnace's list, as the game itself looks it up.
        private static Smelter.ItemConversion Find(Smelter smelter, string ore)
        {
            foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
            {
                if (conversion.m_from != null && conversion.m_from.gameObject.name == ore)
                {
                    return conversion;
                }
            }
            return null;
        }

        // The prefab names of the regular furnace's ores, in its own order, each once.
        public static List<string> RegularOres()
        {
            List<string> ores = new List<string>();
            Smelter smelter = Regular();
            if (smelter == null)
            {
                return ores;
            }
            foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
            {
                string ore = conversion.m_from != null ? conversion.m_from.gameObject.name : null;
                // RegularConversion gives the first entry for an ore, so a second entry for the same ore is skipped.
                if (ore != null && RegularConversion(ore) == conversion)
                {
                    ores.Add(ore);
                }
            }
            return ores;
        }

        public static void Apply(Smelter furnace)
        {
            Smelter blastPrefab = Blast();
            if (Regular() == null || blastPrefab == null)
            {
                return;
            }
            furnace.m_conversion.RemoveAll(c => c.m_from != null && RegularConversion(c.m_from.gameObject.name) != null);
            if (Settings.Enabled.Value)
            {
                foreach (string ore in RegularOres())
                {
                    if (Settings.Ticked(ore))
                    {
                        furnace.m_conversion.Add(RegularConversion(ore));
                    }
                }
            }
            bool on = Settings.Enabled.Value;
            furnace.m_maxOre = on && Settings.OreCapacity.Value > 0 ? Settings.OreCapacity.Value : blastPrefab.m_maxOre;
            furnace.m_maxFuel = on && Settings.CoalCapacity.Value > 0 ? Settings.CoalCapacity.Value : blastPrefab.m_maxFuel;
        }

        public static void ApplyToLoaded()
        {
            foreach (Smelter smelter in UnityEngine.Object.FindObjectsByType<Smelter>(FindObjectsSortMode.None))
            {
                if (IsBlastFurnace(smelter))
                {
                    Apply(smelter);
                }
            }
        }

        // UpdateSmelter's m_secPerProduct and m_fuelPerProduct, for the ore at the front of the queue.
        public static float SecPerProduct(Smelter smelter) =>
            IsFast(smelter) ? Regular().m_secPerProduct / Settings.Speed.Value : smelter.m_secPerProduct;

        public static float CoalPerOre(Smelter smelter) =>
            IsFast(smelter) ? Regular().m_fuelPerProduct * Settings.Coal.Value : smelter.m_fuelPerProduct;

        // An ore that was unticked while some was still in the furnace finishes at the same speed.
        public static bool IsFast(Smelter smelter) =>
            Settings.Enabled.Value && IsBlastFurnace(smelter) && RegularConversion(QueuedOre(smelter)) != null;

        public static void LogRates()
        {
            Smelter smelter = Regular(), blastPrefab = Blast();
            if (smelter == null || blastPrefab == null)
            {
                Plugin.Log.LogWarning("Can't find the regular furnace or the blast furnace (the game changed); the blast furnace stays as the game has it.");
                return;
            }
            string[] missing = Settings.Ores.Keys.Where(ore => RegularConversion(ore) == null).ToArray();
            if (missing.Length > 0)
            {
                Plugin.Log.LogWarning($"The regular furnace doesn't take {string.Join(", ", missing)} (the game changed); the blast furnace won't either.");
            }
            Plugin.Log.LogInfo($"Regular furnace: {smelter.m_secPerProduct:0.#} s and {smelter.m_fuelPerProduct} coal per ore. " +
                $"Blast furnace: {blastPrefab.m_secPerProduct:0.#} s and {blastPrefab.m_fuelPerProduct} coal per ore, holds " +
                $"{blastPrefab.m_maxOre} ore and {blastPrefab.m_maxFuel} coal; the regular furnace's ores " +
                $"({string.Join(", ", RegularOres())}) take {smelter.m_secPerProduct / Settings.Speed.Value:0.##} s and " +
                $"{smelter.m_fuelPerProduct * Settings.Coal.Value:0.##} coal there.");
        }
    }

    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    internal static class ZNetScene_Awake
    {
        private static void Postfix()
        {
            Furnaces.LogRates();
            StationRefunds.Hook();
        }
    }

    // After the game's own Awake (and other mods' prefixes, such as OttoFuel's "Blast Furnace takes all ore"), so the
    // settings decide which of the regular furnace's ores this blast furnace takes.
    [HarmonyPatch(typeof(Smelter), "Awake")]
    internal static class Smelter_Awake
    {
        private static void Postfix(Smelter __instance)
        {
            if (Furnaces.IsBlastFurnace(__instance))
            {
                Furnaces.Apply(__instance);
            }
        }
    }

    // An ore that was unticked (or the mod turned off) while some was still in a blast furnace still comes out as metal,
    // or drops back out if the furnace is broken, instead of vanishing.
    [HarmonyPatch(typeof(Smelter), "GetItemConversion")]
    internal static class Smelter_GetItemConversion
    {
        private static void Postfix(Smelter __instance, string itemName, ref Smelter.ItemConversion __result)
        {
            if (__result == null && Furnaces.IsBlastFurnace(__instance))
            {
                __result = Furnaces.RegularConversion(itemName);
            }
        }
    }

    // MyLittleUI (in The Shack Pack) works out a furnace's "done in" time from m_secPerProduct and m_fuelPerProduct in its
    // hover text. While a blast furnace's hover text is made, those two fields hold the numbers for the ore at the front of
    // the queue; the finalizer puts them back after every postfix has run, even if something throws.
    [HarmonyPatch]
    internal static class Smelter_Hover
    {
        private static Smelter swapped;
        private static float savedSecs;
        private static int savedCoal;

        private static IEnumerable<MethodBase> TargetMethods() =>
            new[] { "OnHoverAddOre", "OnHoverAddFuel", "OnHoverEmptyOre" }
                .Select(name => (MethodBase)AccessTools.Method(typeof(Smelter), name))
                .Where(method => method != null);

        [HarmonyPriority(Priority.First)]
        private static void Prefix(Smelter __instance)
        {
            if (swapped != null || !Furnaces.IsFast(__instance))
            {
                return;
            }
            swapped = __instance;
            savedSecs = __instance.m_secPerProduct;
            savedCoal = __instance.m_fuelPerProduct;
            float secs = Furnaces.SecPerProduct(__instance);
            int coal = Mathf.RoundToInt(Furnaces.CoalPerOre(__instance));
            __instance.m_secPerProduct = secs;
            __instance.m_fuelPerProduct = coal;
        }

        private static void Finalizer()
        {
            if (swapped == null)
            {
                return;
            }
            if (swapped)
            {
                swapped.m_secPerProduct = savedSecs;
                swapped.m_fuelPerProduct = savedCoal;
            }
            swapped = null;
        }
    }

    // In UpdateSmelter, the three reads of m_secPerProduct and the one of m_fuelPerProduct become calls to Furnaces. If
    // the method no longer has exactly those, it is left alone (with a warning): the blast furnace still takes the ores,
    // at its own speed, so a game update can't make the mod misbehave.
    [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
    internal static class Smelter_UpdateSmelter
    {
        private static readonly FieldInfo SecField = AccessTools.Field(typeof(Smelter), nameof(Smelter.m_secPerProduct));
        private static readonly FieldInfo FuelField = AccessTools.Field(typeof(Smelter), nameof(Smelter.m_fuelPerProduct));
        private static readonly MethodInfo SecCall = AccessTools.Method(typeof(Furnaces), nameof(Furnaces.SecPerProduct));
        private static readonly MethodInfo CoalCall = AccessTools.Method(typeof(Furnaces), nameof(Furnaces.CoalPerOre));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            List<CodeInstruction> patched = new List<CodeInstruction>(code);
            int secs = 0, coal = 0;
            for (int i = 0; i < patched.Count; i++)
            {
                if (patched[i].opcode != OpCodes.Ldfld)
                {
                    continue;
                }
                FieldInfo field = patched[i].operand as FieldInfo;
                MethodInfo rate = field == SecField ? SecCall : field == FuelField ? CoalCall : null;
                if (rate == null)
                {
                    continue;
                }
                // The furnace on the stack becomes the call's argument. CoalPerOre returns a float where the field was an
                // int; the conv.r4 that follows leaves a float as it is. A copy keeps any jump labels on the instruction;
                // the original list stays as it was, for the fallback.
                CodeInstruction call = patched[i].Clone();
                call.opcode = OpCodes.Call;
                call.operand = rate;
                patched[i] = call;
                if (rate == SecCall)
                {
                    secs++;
                }
                else
                {
                    coal++;
                }
            }
            if (secs != 3 || coal != 1)
            {
                Plugin.Log.LogWarning($"Smelter.UpdateSmelter reads the smelting time {secs} times and the coal {coal} times instead of " +
                    "3 and 1 (the game changed); the regular furnace's ores smelt at the blast furnace's own speed.");
                return code;
            }
            return patched;
        }
    }
}
