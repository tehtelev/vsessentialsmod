using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace Vintagestory.GameContent
{
    public class AiTaskSeekTargetingEntity : AiTaskSeekEntity
    {
        Entity? guardedEntity;
        Entity? lastattackingEntity;
        long lastattackingEntityFoundMs;

        public AiTaskSeekTargetingEntity(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig)
        {
            searchWaitMs = 1000;
        }

        public override bool ShouldExecute()
        {
            if (entity.World.Rand.NextDouble() < 0.1)
            {
                guardedEntity = GetGuardedEntity();
            }

            if (guardedEntity == null) return false;
            if (entity.WatchedAttributes.GetBool("commandSit") == true) return false;

            if (entity.World.ElapsedMilliseconds - lastattackingEntityFoundMs > 30000 || lastattackingEntity == guardedEntity)
            {
                lastattackingEntity = null;
            }

            return base.ShouldExecute();
        }

        protected override bool GetIsTamed() => true;

        public override void StartExecute()
        {
            base.StartExecute();

            lastattackingEntityFoundMs = entity.World.ElapsedMilliseconds;
            lastattackingEntity = targetEntity;
        }
        public override bool CanSense(Entity e, double range)
        {
            if (!base.CanSense(e, range)) return false;
            if (e == guardedEntity) return false;

            var tasks = e.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager.ActiveTasksBySlot;
            return (e == lastattackingEntity && e.Alive) || tasks?.FirstOrDefault(task => {
                return task is AiTaskBaseTargetable at && at.TargetEntity == guardedEntity && at.AggressiveTargeting;
            }) != null;
        }
    }
}
