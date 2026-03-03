using System;
using HarmonyLib;
using TerrariaModder.Core;
using TerrariaModder.Core.Logging;

namespace Lumberjack
{
    public class Mod : IMod
    {
        public string Id => "lumberjack";
        public string Name => "Lumberjack";
        public string Version => "1.0.0";

        private ILogger _log;
        private Harmony _harmony;

        public void Initialize(ModContext context)
        {
            _log = context.Logger;

            // Patch LogManager.AddRecentLog to intercept log entries
            _harmony = new Harmony("lumberjack");
            _harmony.PatchAll(typeof(LogManagerPatch).Assembly);

            LogConsole.Initialize(_log);
            _log.Info("Lumberjack initialized");
        }

        public void OnWorldLoad() { }
        public void OnWorldUnload() { }

        public void Unload()
        {
            LogConsole.Unload();
            _harmony?.UnpatchAll("lumberjack");
            _log?.Info("Lumberjack unloaded");
        }
    }

    /// <summary>
    /// Harmony postfix patch on LogManager.AddRecentLog to intercept all log entries.
    /// This replaces the user-added OnLogEntry event that was in the modified TerrariaModder.
    /// </summary>
    [HarmonyPatch(typeof(LogManager), "AddRecentLog")]
    internal static class LogManagerPatch
    {
        [HarmonyPostfix]
        static void Postfix(string modId, LogLevel level, string message)
        {
            LogConsole.OnLogEntry(modId, level, message);
        }
    }
}
