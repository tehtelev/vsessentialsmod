using Newtonsoft.Json;
using System;
using System.Text;
using Vintagestory.API;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

#nullable disable

namespace Vintagestory.GameContent
{
    /// <summary>
    /// Allows an entity to get pregnant and give birth.
    /// <br/>Uses the "multiply" code
    /// </summary>
    /// <example><code lang="json">
    ///"behaviors": [
    /// {
    ///     "code": "multiply",
    ///     "enabledByType": {
    ///         "*-female": true,
    ///         "*": false
    ///     },
    ///     "spawnEntityCodes": [{ "code": "sheep-{type}-baby-male" }, { "code": "sheep-{type}-baby-female" }],
    ///     "requiresNearbyEntityCode": "sheep-bighorn-adult-male",
    ///     "requiresNearbyEntityRange": 10,
    ///     "spawnQuantityMin": 1,
    ///     "spawnQuantityMax": 1,
    ///     "pregnancyDays": 20,
    ///     "multiplyCooldownDaysMin": 4,
    ///     "multiplyCooldownDaysMax": 11,
    ///     "portionsEatenForMultiply": 10
    /// },
    ///],
    /// </code></example>
    [DocumentAsJson]
    [AddDocumentationProperty("spawnEntityCode", "Used instead of spawnEntityCodes if the latter is not defined", "System.String", "Optional", "", false)]
    public class EntityBehaviorMultiply : EntityBehaviorMultiplyBase
    {
        long callbackId = 0;


        /// <summary>
        /// The entity codes used to spawn the offspring
        /// </summary>
        [DocumentAsJson("Required", "")]
        protected AssetLocation[] SpawnEntityCodes;

        /// <summary>
        /// Specifies an list of entities required nearby for this entity to be able to get pregnant. Only one needs to match.
        /// </summary>
        [DocumentAsJson("Optional", "")]
        [JsonProperty] public AssetLocation[] RequiresNearbyEntityCodes;
        
        /// <summary>
        /// Specifies an entity required for this entity to be able to get pregnant. Will only set if plural version is not set.
        /// </summary>
        [DocumentAsJson("Optional", "")]
        [JsonProperty] private AssetLocation requiresNearbyEntityCode { set => RequiresNearbyEntityCodes ??= [ value ]; }


        /// <summary>
        /// How long the pregnancy should last in in-game days.
        /// </summary>
        [DocumentAsJson("Optional", "3")]
        [JsonProperty] public double PregnancyDays = 3;

        /// <summary>
        /// Specifies the range within which the entity with code defined in <see cref="RequiresNearbyEntityCode"/> should be located in order for this entity to be able to get pregnant
        /// </summary>
        [DocumentAsJson("Optional", "5")]
        [JsonProperty] public float RequiresNearbyEntityRange = 5;

        /// <summary>
        /// How many offspring should be spawned at minimum
        /// </summary>
        [DocumentAsJson("Optional", "1")]
        [JsonProperty] public float SpawnQuantityMin = 1;

        /// <summary>
        /// How many offspring should be spawned at maximum
        /// </summary>
        [DocumentAsJson("Optional", "2")]
        [JsonProperty] public float SpawnQuantityMax = 2;

        /*internal int GrowthCapQuantity
        {
            get { return attributes["growthCapQuantity"].AsInt(10); }
        }

        internal float GrowthCapRange
        {
            get { return attributes["growthCapRange"].AsFloat(10); }
        }

        internal AssetLocation[] GrowthCapEntityCodes
        {
            get { return AssetLocation.toLocations(attributes["growthCapEntityCodes"].AsStringArray(new string[0])); }
        }*/

        public double TotalDaysLastBirth
        {
            get { return multiplyTree.GetDouble("totalDaysLastBirth", -9999); }
            set { multiplyTree.SetDouble("totalDaysLastBirth", value); entity.WatchedAttributes.MarkPathDirty("multiply"); }
        }

        public double TotalDaysPregnancyStart
        {
            get { return multiplyTree.GetDouble("totalDaysPregnancyStart"); }
            set { multiplyTree.SetDouble("totalDaysPregnancyStart", value); entity.WatchedAttributes.MarkPathDirty("multiply"); }
        }

        public bool IsPregnant
        {
            get { return multiplyTree.GetBool("isPregnant"); }
            set { multiplyTree.SetBool("isPregnant", value); entity.WatchedAttributes.MarkPathDirty("multiply"); }
        }

        public override bool ShouldEat
        {
            get
            {
                return
                    eatAnyway ||
                    (
                        !IsPregnant
                        && GetSaturation() < PortionsEatenForMultiply
                        && TotalDaysCooldownUntil <= entity.World.Calendar.TotalDays
                    )
                ;
            }
        }

        public EntityBehaviorMultiply(Entity entity) : base(entity)
        {

        }

        public override void Initialize(EntityProperties properties, JsonObject attributes)
        {
            base.Initialize(properties, attributes);
            PopulateSpawnEntityCodes(attributes);

            if (entity.World.Side == EnumAppSide.Server)
            {
                if (!multiplyTree.HasAttribute("totalDaysLastBirth"))
                {
                    TotalDaysLastBirth = -9999;
                }

                callbackId = entity.World.RegisterCallback(CheckMultiply, 3000);
            }
        }


        protected virtual void CheckMultiply(float dt)
        {
            if (!entity.Alive)
            {
                callbackId = 0;
                return;
            }

            callbackId = entity.World.RegisterCallback(CheckMultiply, 3000);

            if (entity.World.Calendar == null) return;

            double daysNow = entity.World.Calendar.TotalDays;

            if (!IsPregnant)
            {
                if (TryGetPregnant())
                {
                    IsPregnant = true;
                    TotalDaysPregnancyStart = daysNow;
                }

                return;
            }


            /*if (GrowthCapQuantity > 0 && IsGrowthCapped())
            {
                TimeLastMultiply = entity.World.Calendar.TotalHours;
                return;
            }*/


            if (daysNow - TotalDaysPregnancyStart > PregnancyDays)
            {
                Random rand = entity.World.Rand;

                float q = SpawnQuantityMin + (float)rand.NextDouble() * (SpawnQuantityMax - SpawnQuantityMin);
                TotalDaysLastBirth = daysNow;
                TotalDaysCooldownUntil = daysNow + (MultiplyCooldownDaysMin + rand.NextDouble() * (MultiplyCooldownDaysMax - MultiplyCooldownDaysMin));
                IsPregnant = false;
                entity.WatchedAttributes.MarkPathDirty("multiply");

                GiveBirth(q);
            }

            entity.World.FrameProfiler.Mark("multiply");
        }

        protected virtual void GiveBirth(float q)
        {
            Random rand = entity.World.Rand;

            int generation = entity.WatchedAttributes.GetInt("generation", 0);
            if (SpawnEntityCodes != null)
            {
                while (q >= 1 || rand.NextDouble() < q)
                {
                    q--;
                    AssetLocation SpawnEntityCode = SpawnEntityCodes[rand.Next(SpawnEntityCodes.Length)];
                    EntityProperties childType = entity.World.GetEntityType(SpawnEntityCode);
                    if (childType == null) continue;
                    Entity childEntity = entity.World.ClassRegistry.CreateEntity(childType);

                    childEntity.Pos.SetFrom(entity.Pos);
                    childEntity.Pos.Motion.X += (rand.NextDouble() - 0.5f) / 20f;
                    childEntity.Pos.Motion.Z += (rand.NextDouble() - 0.5f) / 20f;

                    childEntity.Attributes.SetString("origin", "reproduction");
                    childEntity.WatchedAttributes.SetInt("generation", generation + 1);
                    entity.World.SpawnEntity(childEntity);
                }
            }
        }

        protected virtual void PopulateSpawnEntityCodes(JsonObject typeAttributes)
        {
            JsonObject sec = typeAttributes["spawnEntityCodes"];   // Optional fancier syntax in version 1.19+
            if (!sec.Exists)
            {
                sec = typeAttributes["spawnEntityCode"];    // The simple property as it was pre-1.19 - can still be used, suitable for the majority of cases
                if (sec.Exists) SpawnEntityCodes = [ sec.AsString("") ];
                return;
            }
            if (sec.IsArray())
            {
                SpawnEntityProperties[] codes = sec.AsArray<SpawnEntityProperties>();
                SpawnEntityCodes = new AssetLocation[codes.Length];
                for (int i = 0; i < codes.Length; i++) SpawnEntityCodes[i] = new AssetLocation(codes[i].Code ?? "");
            }
            else
            {
                SpawnEntityCodes = [ sec.AsString("") ];
            }
        }


        public override void TestCommand(object arg)
        {
            GiveBirth((int) arg);
        }

        protected virtual bool TryGetPregnant()
        {
            if (entity.World.Rand.NextDouble() > 0.06) return false;
            if (TotalDaysCooldownUntil > entity.World.Calendar.TotalDays) return false;

            ITreeAttribute tree = entity.WatchedAttributes.GetTreeAttribute("hunger");
            if (tree == null) return false;

            float saturation = tree.GetFloat("saturation", 0);

            if (saturation >= PortionsEatenForMultiply)
            {
                Entity maleentity = null;
                if (RequiresNearbyEntityCodes != null && (maleentity = GetRequiredEntityNearby()) == null) return false;

                if (entity.World.Rand.NextDouble() < 0.2)
                {
                    tree.SetFloat("saturation", saturation - 1);
                    return false;
                }

                tree.SetFloat("saturation", saturation - PortionsEatenForMultiply);

                if (maleentity != null)
                {
                    ITreeAttribute maletree = maleentity.WatchedAttributes.GetTreeAttribute("hunger");
                    if (maletree != null)
                    {
                        saturation = maletree.GetFloat("saturation", 0);
                        maletree.SetFloat("saturation", Math.Max(0, saturation - 1));
                    }
                }

                IsPregnant = true;
                TotalDaysPregnancyStart = entity.World.Calendar.TotalDays;
                entity.WatchedAttributes.MarkPathDirty("multiply");

                return true;
            }

            return false;
        }

        protected virtual Entity GetRequiredEntityNearby()
        {
            if (RequiresNearbyEntityCodes == null) return null;

            return entity.World.GetNearestEntity(entity.Pos.XYZ, RequiresNearbyEntityRange, RequiresNearbyEntityRange, (e) =>
            {
                bool matches = false;
                foreach (AssetLocation sire in RequiresNearbyEntityCodes)
                {
                    if (e.WildCardMatch(sire)) {
                        matches = true;
                        break;
                    }
                }

                return matches && EntityCanMate(e);
            });
        }

        protected virtual bool EntityCanMate(Entity e)
        {
            return e.Alive && !e.WatchedAttributes.GetBool("doesEat") || (e.WatchedAttributes["hunger"] as ITreeAttribute)?.GetFloat("saturation") >= 1;
        }

        public override void OnEntityDespawn(EntityDespawnData despawn)
        {
            entity.World.UnregisterCallback(callbackId);
        }



        public override string PropertyName()
        {
            return "multiply";
        }

        public override void GetInfoText(StringBuilder infotext)
        {
            multiplyTree = entity.WatchedAttributes.GetTreeAttribute("multiply");

            if (IsPregnant) infotext.AppendLine(Lang.Get("Is pregnant"));
            else
            {
                if (entity.Alive)
                {
                    ITreeAttribute tree = entity.WatchedAttributes.GetTreeAttribute("hunger");
                    if (tree != null)
                    {
                        float saturation = tree.GetFloat("saturation", 0);
                        infotext.AppendLine(Lang.Get("Portions eaten: {0}", saturation));
                    }

                    double daysLeft = TotalDaysCooldownUntil - entity.World.Calendar.TotalDays;

                    if (daysLeft > 0)
                    {
                        if (daysLeft > 3)
                        {
                            infotext.AppendLine(Lang.Get("Several days left before ready to mate"));
                        }
                        else
                        {
                            infotext.AppendLine(Lang.Get("Less than 3 days before ready to mate"));
                        }

                    }
                    else
                    {
                        infotext.AppendLine(Lang.Get("Ready to mate"));
                    }
                }
            }
        }
    }

    public class SpawnEntityProperties
    {
        [JsonProperty]
        public string Code;
    }
}
