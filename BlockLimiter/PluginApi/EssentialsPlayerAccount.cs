using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.Managers;

namespace BlockLimiter.PluginApi
{
    public class EssentialsPlayerAccount
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static string EssentialGuid = "cbfdd6ab-4cda-4544-a201-f73efa3d46c0";
        private static ITorchPlugin EssentialsPlugin;

        private static MethodInfo GetRankMethod;
        public static bool EssentialsInstalled => EssentialsPlugin != null;

        public static bool InitializeCommunication()
        {
            var pluginId = new Guid(EssentialGuid);
            var pluginManager = BlockLimiter.Instance.Torch.Managers.GetManager<PluginManager>();
            var result = false;

            try
            {
                if (!pluginManager.Plugins.TryGetValue(pluginId, out EssentialsPlugin) || EssentialsPlugin == null)
                {
                    Log.Warn("Communication with Essentials failed");
                }
                else
                {
                    Log.Info("BlockLimiter communication with Essentials successful");
                    result = true;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Communication with Essentials failed");
            }

            return result;
        }

        private static bool GetRankMethodInfo()
        {
            if (GetRankMethod != null && EssentialsInstalled)
                return true;

            try
            {
                // Corrigido: o método está em Essentials.PlayerAccountModule e se chama "GetRank"
                var playerAccountModuleType = EssentialsPlugin.GetType().Assembly.GetType("Essentials.PlayerAccountModule");
                if (playerAccountModuleType == null)
                {
                    Log.Warn("Failed to locate Essentials.PlayerAccountModule type");
                    return false;
                }

                GetRankMethod = playerAccountModuleType.GetMethod("GetRank", BindingFlags.Public | BindingFlags.Instance);
                if (GetRankMethod == null)
                {
                    Log.Warn("Failed to get GetRank method from Essentials.PlayerAccountModule");
                    return false;
                }

                Log.Info("Successfully linked GetRank method from Essentials");
                return true;
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to link GetRank method");
                return false;
            }
        }

        public static string GetPrimaryRank(ulong steamId)
        {
            if (steamId == 0 || !EssentialsInstalled)
                return string.Empty;

            try
            {
                if (!GetRankMethodInfo())
                {
                    Log.Warn("Essentials rank method not found");
                    return string.Empty;
                }

                // cria uma instância da classe Essentials.PlayerAccountModule
                var playerAccountModuleType = EssentialsPlugin.GetType().Assembly.GetType("Essentials.PlayerAccountModule");
                var instance = Activator.CreateInstance(playerAccountModuleType);

                var result = GetRankMethod.Invoke(instance, new object[] { steamId }) as string;

                return result ?? string.Empty;
            }
            catch (Exception e)
            {
                Log.Warn(e, "Failed to get player rank from Essentials");
                return string.Empty;
            }
        }
    }
}
