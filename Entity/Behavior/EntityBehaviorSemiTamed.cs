using Vintagestory.API;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

#nullable disable

namespace Vintagestory.GameContent
{
    /// <summary>
    /// Causes an entity to not flee by default when near a player.
    /// </summary>
    [DocumentAsJson]
    public class EntityBehaviorSemiTamed : EntityBehavior
    {
        public EntityBehaviorSemiTamed(Entity entity) : base(entity)
        {
            
        }

        public override void AfterInitialized(bool onFirstSpawn)
        {
            base.AfterInitialized(onFirstSpawn);
            entity.GetBehavior<EntityBehaviorTaskAI>().TaskManager.OnShouldExecuteTask += TaskManager_OnShouldExecuteTask;
        }

        private bool TaskManager_OnShouldExecuteTask(IAiTask t)
        {
            if (t is AiTaskFleeEntity fleetask)
            {
                return fleetask.WhenInEmotionStates == null && fleetask.targetEntity is not EntityPlayer;
            }
            return true;
        }

        public override string PropertyName() => "semitamed";
    }
}
