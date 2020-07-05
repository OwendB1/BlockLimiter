using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlockLimiter.Settings;
using NLog.Fluent;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;

namespace BlockLimiter.Utility
{
    public static class UpdateLimits
    {

        public static void PlayerLimit(long id)
        {
            if (id == 0) return;
            var blockCache = new HashSet<MySlimBlock>();
            var playerBlocks = new HashSet<MySlimBlock>();
            
            
            GridCache.GetBlocks(blockCache);
            if (blockCache.Count == 0)
            {
                return;
            }
            
            playerBlocks.UnionWith(blockCache.Where(x=> Block.IsOwner(x,id)));

            if (playerBlocks.Count == 0)
            {
                foreach (var limit in BlockLimiterConfig.Instance.AllLimits)
                {
                    limit.FoundEntities.Remove(id);
                }
                return;
            }

            Parallel.ForEach(BlockLimiterConfig.Instance.AllLimits, limit =>
            {
                if (!limit.LimitPlayers)
                {
                    limit.FoundEntities.Remove(id);
                    return;
                }
                var limitedBlocks = playerBlocks.Count(x =>
                    Block.IsMatch(x.BlockDefinition, limit));
                if (limitedBlocks == 0)
                {
                    limit.FoundEntities.Remove(id);
                    return;
                }

                limit.FoundEntities[id] = limitedBlocks;


            });


        }

        
        public static void GridLimit(MyCubeGrid grid)
        {
            if (grid == null) return;
            
            var blocks = new HashSet<MySlimBlock>();
            blocks.UnionWith(grid.CubeBlocks);    
            
            if (blocks.Count == 0) return;

            Parallel.ForEach(BlockLimiterConfig.Instance.AllLimits, limit =>
            {
                if (!limit.LimitGrids)
                {
                    limit.FoundEntities.Remove(grid.EntityId);
                    return;
                }
                
                if (!Grid.IsGridType(grid,limit))
                {
                    limit.FoundEntities.Remove(grid.EntityId);
                    return;
                }

                var limitedBlocks = blocks.Count(x => Block.IsMatch(x.BlockDefinition, limit));

                if (limitedBlocks == 0)
                {
                    limit.FoundEntities.Remove(grid.EntityId);
                    return;
                }
                limit.FoundEntities[grid.EntityId] = limitedBlocks;

            });

        }

        
        public static void FactionLimit(long id)
        {
            if (id == 0) return;
            var blockCache = new HashSet<MySlimBlock>();
            var factionBlocks = new HashSet<MySlimBlock>();
            
            var faction = MySession.Static.Factions.TryGetFactionById(id);
            var limits = BlockLimiterConfig.Instance.AllLimits;
            if (faction == null) return;
            
            GridCache.GetBlocks(blockCache);
            if (blockCache.Count == 0)
                return;
            
            factionBlocks.UnionWith(blockCache.Where(x => x.FatBlock?.GetOwnerFactionTag() == faction.Tag));

            if (factionBlocks.Count == 0)
            {
                foreach (var limit in limits)
                {
                  limit.FoundEntities.Remove(id);  
                }
                return;
            }

            Parallel.ForEach(limits, limit =>
            {
                if (!limit.LimitFaction)
                {
                    limit.FoundEntities.Remove(id);
                    return;
                }

                var factionBlockCount = factionBlocks.Count(x => Block.IsMatch(x.BlockDefinition, limit));

                if (factionBlockCount == 0)
                {
                    limit.FoundEntities.Remove(id);
                    return;
                }

                limit.FoundEntities[id] = factionBlockCount;

            });

        }
        
    }
}