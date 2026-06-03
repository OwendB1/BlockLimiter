using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using BlockLimiter.Utility;
using NLog;
using Sandbox.Game.World;

namespace BlockLimiter.Settings
{
    public class PlayerTimeModule
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(List<PlayerTimeData>));

        public static List<PlayerTimeData> PlayerTimes = new List<PlayerTimeData>();

        [Serializable]
        public class PlayerTimeData 
        {
            [XmlElement(Order = 1)]
            public string Player { get; set; }
            [XmlElement(Order = 2)]
            public ulong SteamId { get; set; }
            [XmlElement(Order = 3)]
            public DateTime FirstLogTime { get; set; }
        }


        private static void SaveTimeData()
        {
            if (PlayerTimes == null) PlayerTimes = new List<PlayerTimeData>();

            using (var writer = new StreamWriter(BlockLimiter.Instance.timeDataPath))
            {
                Serializer.Serialize(writer, PlayerTimes);
            }
        }

        public static void LoadTimeData()
        {
            if (!File.Exists(BlockLimiter.Instance.timeDataPath))
            {
                PlayerTimes = new List<PlayerTimeData>();
                SaveTimeData();
                return;
            }

            if (new FileInfo(BlockLimiter.Instance.timeDataPath).Length == 0)
            {
                PlayerTimes = new List<PlayerTimeData>();
                SaveTimeData();
                return;
            }

            using (var reader = new StreamReader(BlockLimiter.Instance.timeDataPath))
            {
                PlayerTimes = (List<PlayerTimeData>) Serializer.Deserialize(reader) ?? new List<PlayerTimeData>();
            }
        }

        public static void LogTime(Torch.API.IPlayer player)
        {
            if (player == null) return;
            ulong steamId = player.SteamId;
            PlayerTimeData data = new PlayerTimeData();
            bool found = false;
            if (PlayerTimes == null) PlayerTimes = new List<PlayerTimeData>();
            foreach (var time in PlayerTimes)
            {
                if (time.SteamId != steamId) continue;
                found = true;
                break;
            }

            if (found) return;
            Log.Info($"Logging time for player {player.Name}");
            data.SteamId = steamId;
            data.Player = player.Name;
            var lastLogout = MySession.Static.Players.TryGetIdentity(Utilities.GetPlayerIdFromSteamId(steamId))?.LastLoginTime;
            if (lastLogout != null && DateTime.Now > lastLogout)
                data.FirstLogTime = (DateTime) lastLogout;
            else
            {
                data.FirstLogTime = DateTime.Now;
            }
            PlayerTimes.Add(data);
            SaveTimeData();
        }

        public static DateTime GetTime(ulong steamId)
        {
            var time = DateTime.Now;

            foreach (var data in PlayerTimes)
            {
                if (data.SteamId != steamId) continue;
                time = data.FirstLogTime;
                break;
            }

            return time;
        }
    }
}
