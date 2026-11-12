using System;

// A local stand-in for HarmonyLib: the analyzer matches the attribute by name,
// so the real Harmony assembly is not needed to exercise patch-target checking.
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type declaringType) { }
        public HarmonyPatch(string methodName) { }
        public HarmonyPatch(Type declaringType, string methodName) { }
    }
}

namespace TestMod
{
    /// <summary>
    /// Compiled against game API v1. "SetName" survives into v2, "LegacyCall" does not,
    /// so only the second target may be reported as missing.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(GameCode.Block))]
    [HarmonyLib.HarmonyPatch("SetName")]
    internal static class PatchSetName
    {
        private static void Postfix() { }
    }

    [HarmonyLib.HarmonyPatch(typeof(GameCode.Block))]
    [HarmonyLib.HarmonyPatch("LegacyCall")]
    internal static class PatchLegacyCall
    {
        private static void Postfix() { }
    }
}
