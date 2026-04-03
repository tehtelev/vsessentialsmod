using System;
using Vintagestory.API;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

#nullable disable

namespace Vintagestory.GameContent
{
    /// <summary>
    /// Revives the entity a period of time after its death.
    /// <br/>Uses the "reviveondeath" code
    /// </summary>
    /// <example><code lang="json">
    /// "behaviors": [
    ///  {
    ///     "code": "reviveondeath"
    ///     "minHours": 24,
    ///     "maxHours": 72
    ///  }
    /// ]
    /// </code></example>
    [DocumentAsJson]
    public class EntityBehaviorReviveOnDeath : EntityBehavior
    {
        /// <summary>
        /// Minimum in-game hours until the entity is revived
        /// </summary>
        [DocumentAsJson("Optional", "24")]
        float minHours;

        /// <summary>
        /// Maximum in-game hours until the entity is revived
        /// </summary>
        [DocumentAsJson("Optional", "48")]
        float maxHours;

        public double DiedTotalHours
        {
            get { return entity.Attributes.GetDouble("diedTotalHours");}
            set { entity.Attributes.SetDouble("diedTotalHours", value); }
        }

        public double ReviveWaitHours
        {
            get { return entity.Attributes.GetDouble("reviveWaitHours"); }
            set { entity.Attributes.SetDouble("reviveWaitHours", value); }
        }

        public EntityBehaviorReviveOnDeath(Entity entity) : base(entity)
        {
            if (!(entity is EntityAgent)) throw new InvalidOperationException("Reive on death behavior is only possible on entities deriving from EntityAgent");
            (entity as EntityAgent).AllowDespawn = false;
        }

        public override void Initialize(EntityProperties properties, JsonObject typeAttributes)
        {
            this.minHours = (float)typeAttributes["minHours"].AsFloat(24);
            this.maxHours = (float)typeAttributes["maxHours"].AsFloat(48);
        }

        public override void OnGameTick(float deltaTime)
        {
            if (entity.Alive || entity.ShouldDespawn) return;
            
            if (entity.World.Calendar.TotalHours > DiedTotalHours + ReviveWaitHours)
            {
                entity.Revive();
            }
        }

        public override void OnEntityDeath(DamageSource damageSourceForDeath)
        {
            DiedTotalHours = entity.World.Calendar.TotalHours;
            ReviveWaitHours = minHours + entity.World.Rand.NextDouble() * (maxHours - minHours);
            base.OnEntityDeath(damageSourceForDeath);
        }

        public override string PropertyName()
        {
            return "timeddespawn";
        }
    }
}
