using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

#nullable disable

namespace Vintagestory.GameContent
{
    /// <summary>
    /// Client-side particle system for falling blocks.
    /// Tracks active EntityBlockFalling instances and spawns dust and debris for them in an async thread.
    /// </summary>
    public class FallingBlockParticlesModSystem : ModSystem
    {
        // Dust particles — quad-shaped, slowly rise upward and dissipate
        public static SimpleParticleProperties dustParticles;
        // Debris particles — cube-shaped, scatter sideways under gravity
        static SimpleParticleProperties bitsParticles;

        static FallingBlockParticlesModSystem()
        {
            // Dust: large semi-transparent quads, drift upward, no gravity, self-propelled
            dustParticles = new SimpleParticleProperties(1, 3, ColorUtil.ToRgba(40, 220, 220, 220), new Vec3d(), new Vec3d(), new Vec3f(-0.25f, -0.25f, -0.25f), new Vec3f(0.25f, 0.25f, 0.25f), 1, 1, 0.3f, 0.3f, EnumParticleModel.Quad);
            dustParticles.AddQuantity = 5;
            dustParticles.MinVelocity.Set(-0.05f, -0.4f, -0.05f);
            dustParticles.AddVelocity.Set(0.1f, 0.2f, 0.1f);
            dustParticles.WithTerrainCollision = true;
            dustParticles.ParticleModel = EnumParticleModel.Quad;
            dustParticles.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.QUADRATIC, -16f);
            dustParticles.SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.QUADRATIC, 3f);
            dustParticles.GravityEffect = 0;
            dustParticles.MaxSize = 1.3f;
            dustParticles.LifeLength = 3f;
            dustParticles.SelfPropelled = true;
            dustParticles.AddPos.Set(1.4, 1.4, 1.4);

            // Debris: small cubes, scatter on impact, strong gravity
            bitsParticles = new SimpleParticleProperties(1, 3, ColorUtil.ToRgba(40, 220, 220, 220), new Vec3d(), new Vec3d(), new Vec3f(-0.25f, -0.25f, -0.25f), new Vec3f(0.25f, 0.25f, 0.25f), 1, 1, 0.1f, 0.3f, EnumParticleModel.Quad);
            bitsParticles.AddPos.Set(1.4, 1.4, 1.4);
            bitsParticles.AddQuantity = 20;
            bitsParticles.MinVelocity.Set(-0.25f, 0, -0.25f);
            bitsParticles.AddVelocity.Set(0.5f, 1, 0.5f);
            bitsParticles.WithTerrainCollision = true;
            bitsParticles.ParticleModel = EnumParticleModel.Cube;
            bitsParticles.LifeLength = 1.5f;
            bitsParticles.SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.QUADRATIC, -0.5f);
            bitsParticles.GravityEffect = 2.5f;
            bitsParticles.MinSize = 0.5f;
            bitsParticles.MaxSize = 1.5f;
        }

        ICoreClientAPI capi;

        // Active falling blocks for which particles should be spawned
        HashSet<EntityBlockFalling> fallingBlocks = new HashSet<EntityBlockFalling>();

        // Thread-safe queues for registration/removal between the game thread and particle thread
        ConcurrentQueue<EntityBlockFalling> toRegister = new ConcurrentQueue<EntityBlockFalling>();
        ConcurrentQueue<EntityBlockFalling> toRemove = new ConcurrentQueue<EntityBlockFalling>();

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            // Particles are only needed on the client
            return forSide == EnumAppSide.Client;
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            this.capi = api;
            // Register the async spawner — it runs in a separate thread every frame
            api.Event.RegisterAsyncParticleSpawner(asyncParticleSpawn);
        }

        /// <summary>
        /// Add a block to the tracked list for particle spawning.
        /// Called from EntityBlockFalling.Initialize on the client.
        /// </summary>
        public void Register(EntityBlockFalling entity)
        {
            toRegister.Enqueue(entity);
        }

        /// <summary>
        /// Remove a block from the tracked list.
        /// Called on despawn or after the block hits the ground.
        /// </summary>
        public void Unregister(EntityBlockFalling entity)
        {
            toRemove.Enqueue(entity);
        }

        /// <summary>
        /// Number of active falling blocks currently being tracked for particle spawning.
        /// </summary>
        public int ActiveFallingBlocks => fallingBlocks.Count;


        /// <summary>
        /// Async callback invoked every frame in the particle thread.
        /// Spawns dust and debris for each tracked block.
        /// </summary>
        private bool asyncParticleSpawn(float dt, IAsyncParticleManager manager)
        {
            int alive = manager.ParticlesAlive(EnumParticleModel.Quad);

            // The more particles already in the air, the fewer new ones we spawn.
            // Graph: http://fooplot.com/#W3sidHlwZSI6MCwiZXEiOiIwLjk1Xih4LzUpIiwiY29sb3IiOiIjMDAwMDAwIn0seyJ0eXBlIjoxMDAwLCJ3aW5kb3ciOlsiMCIsIjIwMCIsIjAiLCIxIl19XQ--
            float particlemul = Math.Max(0.05f, (float)Math.Pow(0.95f, alive / 200f));

            foreach (var bef in fallingBlocks)
            {
                float dustAdd = 0f;

                // On ground impact — one-time dust burst (only if the block is not underwater)
                if (bef.nowImpacted)
                {
                    var lblock = capi.World.BlockAccessor.GetBlock(bef.Pos.AsBlockPos, BlockLayersAccess.Fluid);
                    // No dust under water
                    if (lblock.Id == 0)
                    {
                        dustAdd = 20f;
                    }
                    bef.nowImpacted = false;
                }

                if (bef.Block.Id != 0)
                {
                    // Sample color from the block's first drop for particle tinting
                    dustParticles.Color = bef.stackForParticleColor.Collectible.GetRandomColor(capi, bef.stackForParticleColor);
                    dustParticles.Color &= 0xffffff;
                    dustParticles.Color |= (150 << 24); // Semi-transparency
                    dustParticles.MinPos.Set(bef.Pos.X - 0.2 - 0.5, bef.Pos.Y, bef.Pos.Z - 0.2 - 0.5);
                    dustParticles.MinSize = 1f;

                    // Dust emission intensity scales with fall speed
                    float veloMul = dustAdd / 20f;
                    dustParticles.AddPos.Y = bef.maxSpawnHeightForParticles;
                    dustParticles.MinVelocity.Set(-0.2f * veloMul, 1f * (float)bef.Pos.Motion.Y, -0.2f * veloMul);
                    dustParticles.AddVelocity.Set(0.4f * veloMul, 0.2f * (float)bef.Pos.Motion.Y + -veloMul, 0.4f * veloMul);
                    dustParticles.MinQuantity = dustAdd * bef.dustIntensity * particlemul / 2f;
                    dustParticles.AddQuantity = (6 * Math.Abs((float)bef.Pos.Motion.Y) + dustAdd) * bef.dustIntensity * particlemul / 2f;

                    manager.Spawn(dustParticles);
                }

                // Debris is always spawned while the block is falling
                bitsParticles.MinPos.Set(bef.Pos.X - 0.2 - 0.5, bef.Pos.Y - 0.5, bef.Pos.Z - 0.2 - 0.5);

                // Debris downward velocity is proportional to the block's fall speed
                bitsParticles.MinVelocity.Set(-2f, 30f * (float)bef.Pos.Motion.Y, -2f);
                bitsParticles.AddVelocity.Set(4f, 0.2f * (float)bef.Pos.Motion.Y, 4f);
                bitsParticles.MinQuantity = particlemul;
                bitsParticles.AddQuantity = 6 * Math.Abs((float)bef.Pos.Motion.Y) * particlemul;
                bitsParticles.Color = dustParticles.Color;
                bitsParticles.AddPos.Y = bef.maxSpawnHeightForParticles;

                dustParticles.Color = bef.Block.GetRandomColor(capi, bef.stackForParticleColor);

                capi.World.SpawnParticles(bitsParticles);
            }

            // Process the register/remove queues at the end of the frame
            // to avoid modifying the collection during iteration
            int cnt = toRegister.Count;
            while (cnt-- > 0)
            {
                if (toRegister.TryDequeue(out EntityBlockFalling bef))
                {
                    fallingBlocks.Add(bef);
                }
            }

            cnt = toRemove.Count;
            while (cnt-- > 0)
            {
                if (toRemove.TryDequeue(out EntityBlockFalling bef))
                {
                    fallingBlocks.Remove(bef);
                }
            }

            return true;
        }
    }


    /// <summary>
    /// Server-side spawn manager for falling blocks.
    /// Limits the number of concurrently existing EntityBlockFalling instances,
    /// queues spawn requests, and performs instant simulation for blocks outside player range.
    /// </summary>
    public class FallingSpawnManager : ModSystem
    {
        // Total number of loaded EntityBlockFalling instances on the server
        private static int totalFallingBlocks = 0;
        // Queue of spawn requests waiting for a free slot
        private static Queue<SpawnRequest> requestQueue = new Queue<SpawnRequest>();
        // Positions that already have a pending request — prevents duplicates
        private static HashSet<BlockPos> pendingPositions = new HashSet<BlockPos>();
        // Maximum number of concurrently existing EntityBlockFalling instances
        private static int maxFallingBlocks;
        private static ICoreServerAPI sapi;
        // Radius around a player within which entities are created (in blocks)
        private static int activeRange = 128;


        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Server;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            maxFallingBlocks = 200; // TODO: move to server magic numbers config!

            // Determine entity tracking radius from server settings
            try
            {
                int trackingChunks = api.World.DefaultEntityTrackingRange;
                activeRange = trackingChunks * GlobalConstants.ChunkSize;
            }
            catch { /* keep default of 128 */ }

            api.Event.OnEntitySpawn += OnEntitySpawn;
            api.Event.OnEntityLoaded += OnEntityLoaded;
            api.Event.OnEntityDespawn += OnEntityDespawn;
            // Spawn queue tick — every 32 ms
            api.Event.RegisterGameTickListener(OnGameTick, 32);
        }

        // Counters: only track EntityBlockFalling, not living creatures
        private void OnEntitySpawn(Entity entity)
        {
            if (!entity.IsCreature && entity is EntityBlockFalling) totalFallingBlocks++;
        }

        private void OnEntityLoaded(Entity entity)
        {
            if (!entity.IsCreature && entity is EntityBlockFalling) totalFallingBlocks++;
        }

        private void OnEntityDespawn(Entity entity, EntityDespawnData despawn)
        {
            if (!entity.IsCreature && entity is EntityBlockFalling) totalFallingBlocks--;
        }

        /// <summary>
        /// Process the spawn queue each game tick.
        /// Spawn as many entities as the limit allows.
        /// </summary>
        private void OnGameTick(float dt)
        {
            SpawnRequest request;
            Block block;
            Entity existing;

            while (totalFallingBlocks < maxFallingBlocks && requestQueue.Count > 0)
            {
                request = requestQueue.Dequeue();
                pendingPositions.Remove(request.InitialPos);

                // Verify the block at the source position hasn't changed while the request was queued
                block = sapi.World.BlockAccessor.GetBlock(request.InitialPos);
                if (block == null || block.Id == 0 || block != request.Block)
                    continue;

                // If a falling entity already exists at this position — defer the spawn
                existing = sapi.World.GetNearestEntity(
                    request.InitialPos.ToVec3d().Add(0.5, 0.5, 0.5), 1, 1.5f,
                    e => !e.IsCreature && e is EntityBlockFalling ebf && ebf.initialPos.Equals(request.InitialPos));

                if (existing != null)
                {
                    request.RetryCount++;
                    if (request.RetryCount >= 10)
                    {
                        // After 10 failed attempts — give up and drop items
                        var drops = request.Block.GetDrops(sapi.World, request.InitialPos, null);
                        EntityBlockFalling.SpawnDrops(sapi.World, request.InitialPos, drops, request.BlockEntity);
                        continue;
                    }
                    // Return the request to the back of the queue for another attempt
                    pendingPositions.Add(request.InitialPos);
                    requestQueue.Enqueue(request);
                    continue;
                }

                // Create the entity and apply position offset if specified
                EntityBlockFalling entityBf = new EntityBlockFalling(
                    request.Block, request.BlockEntity, request.InitialPos,
                    request.FallSound, request.ImpactDamageMul,
                    request.CanFallSideways, request.DustIntensity)
                {
                    DoRemoveBlock = request.DoRemoveBlock
                };

                sapi.World.SpawnEntity(entityBf);

                if (request.PositionOffset != null && request.PositionOffset != Vec3d.Zero)
                {
                    entityBf.Pos.X += request.PositionOffset.X;
                    entityBf.Pos.Y += request.PositionOffset.Y;
                    entityBf.Pos.Z += request.PositionOffset.Z;
                }
            }
        }

        /// <summary>
        /// Requests a block to fall.
        /// If a player is nearby — creates an entity (queued if at the limit).
        /// If no players are nearby — performs instant simulation without an entity.
        /// </summary>
        public void RequestSpawn(Block block, BlockEntity be, BlockPos initialPos,
                                 AssetLocation fallSound, float impactDamageMul,
                                 bool canFallSideways, float dustIntensity,
                                 bool doRemoveBlock = true, Vec3d positionOffset = null)
        {
            // Skip duplicates — this position already has a pending request
            if (pendingPositions.Contains(initialPos))
                return;

            // Check whether at least one player is within activeRange
            bool hasPlayerNearby = false;
            Vec3d posVec = initialPos.ToVec3d();
            EntityPlayer eplr;
            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                eplr = player.Entity;
                if (eplr != null && eplr.Pos.InRangeOf(posVec, activeRange * activeRange, activeRange))
                {
                    hasPlayerNearby = true;
                    break;
                }
            }

            if (!hasPlayerNearby)
            {
                // No players nearby — no need to create an entity, simulate instantly
                InstantFallSimulation(block, be, initialPos, fallSound, impactDamageMul,
                                      canFallSideways, dustIntensity, doRemoveBlock, positionOffset);
                return;
            }

            // Player is nearby — add to queue to create a full entity
            pendingPositions.Add(initialPos);
            requestQueue.Enqueue(new SpawnRequest
            {
                Block = block,
                BlockEntity = be,
                InitialPos = initialPos.Copy(),
                FallSound = fallSound,
                ImpactDamageMul = impactDamageMul,
                CanFallSideways = canFallSideways,
                DustIntensity = dustIntensity,
                DoRemoveBlock = doRemoveBlock,
                PositionOffset = positionOffset ?? Vec3d.Zero
            });
        }

        /// <summary>
        /// Thin wrapper: retrieves drops and delegates simulation to the static EntityBlockFalling method.
        /// </summary>
        private void InstantFallSimulation(Block block, BlockEntity be, BlockPos initialPos,
                                           AssetLocation fallSound, float impactDamageMul,
                                           bool canFallSideways, float dustIntensity,
                                           bool doRemoveBlock, Vec3d positionOffset)
        {
            var drops = block.GetDrops(sapi.World, initialPos, null);
            EntityBlockFalling.SimulateInstantFall(sapi.World, block, be, initialPos, drops, doRemoveBlock);
        }

        public override void Dispose()
        {
            // On mod unload, clear the queue and unsubscribe from events to avoid holding world object references
            requestQueue.Clear();
            pendingPositions.Clear();
            totalFallingBlocks = 0;
            if (sapi != null)
            {
                sapi.Event.OnEntitySpawn -= OnEntitySpawn;
                sapi.Event.OnEntityLoaded -= OnEntityLoaded;
                sapi.Event.OnEntityDespawn -= OnEntityDespawn;
            }
        }

        /// <summary>
        /// Data for a single spawn request held in the queue.
        /// </summary>
        private struct SpawnRequest
        {
            public Block Block;
            public BlockEntity BlockEntity;
            public BlockPos InitialPos;
            public AssetLocation FallSound;
            public float ImpactDamageMul;
            public bool CanFallSideways;
            public float DustIntensity;
            public bool DoRemoveBlock;
            public Vec3d PositionOffset;
            public int RetryCount; // How many times this request has been returned to the queue due to a occupied position
        }
    }


    /// <summary>
    /// Entity representing a falling block (sand, gravel, etc.).
    /// Removes the block from its initial position on spawn,
    /// and on landing either places the block at the final position or drops items.
    /// </summary>
    public class EntityBlockFalling : Entity
    {
        // Packet ID used to synchronize the moment of impact with the client
        private const int packetIdMagicNumber = 1234;
        // Set of EntityIds for blocks whose fall sound is currently playing
        static public HashSet<long> fallingNow = new HashSet<long>();

        // Order in which lateral directions are checked (randomized per EntityId for variety)
        private readonly List<int> fallDirections = new() { 0, 1, 2, 3 };
        // Last lateral slide direction (prevents sliding back the way we came)
        private int lastFallDirection = 0;
        // Height of the "hop" when stuck — increases with each failed attempt
        private int hopUpHeight = 1;
        private FallingBlockParticlesModSystem particleSys;
        // Number of ticks since spawn — delay before physics begin
        private int ticksAlive;
        // Remaining ticks before the entity dies after hitting the ground
        private int lingerTicks;
        private AssetLocation blockCode;
        // Items that will drop if the block cannot be placed
        private ItemStack[] drops;
        // Damage multiplier applied when entities are hit by the falling block
        private float impactDamageMul;
        // Flag: fall has already been handled, no repeat processing needed
        private bool fallHandled;
        private byte[] lightHsv;
        private AssetLocation fallSound;
        private ILoadedSound sound;
        // Delay before the sound starts — small random offset for naturalness
        private float soundStartDelay;
        private bool canFallSideways;
        // Horizontal velocity during lateral sliding, decays over time
        private Vec3d fallMotion = new Vec3d();
        // Accumulator for entity push damage (applied once per 0.2 s)
        private float pushaccum;

        internal float dustIntensity;
        // Item stack used to determine particle color
        internal ItemStack stackForParticleColor;
        // True at the moment of ground impact — triggers the particle burst
        internal bool nowImpacted;

        // Flag: the initial block has already been removed from the world (needed for client sync)
        public bool InitialBlockRemoved;
        // Block position before the fall began
        public BlockPos initialPos;
        // Serialized BlockEntity state (if the block had one)
        public TreeAttribute blockEntityAttributes;
        public string blockEntityClass;
        // Reference to the BlockEntity before it was removed (server-side only)
        public BlockEntity removedBlockentity;
        // If false — the block is not removed in Initialize (used for special effects)
        public bool DoRemoveBlock = true;
        // Height above the block position at which particles are spawned
        public float maxSpawnHeightForParticles = 1.4f;

        public EntityBlockFalling() { }
        // Very high density — block sinks in liquids instead of floating
        public override float MaterialDensity => 99999;
        public override byte[] LightHsv => lightHsv;

        // The block never "freezes" (never enters sleep mode) — always ticks
        public override bool AlwaysActive => true;


        public EntityBlockFalling(Block block, BlockEntity blockEntity, BlockPos initialPos, AssetLocation fallSound, float impactDamageMul, bool canFallSideways, float dustIntensity)
        {
            this.impactDamageMul = impactDamageMul;
            this.fallSound = fallSound;
            this.canFallSideways = canFallSideways;
            this.dustIntensity = dustIntensity;

            // Store parameters in WatchedAttributes for client synchronization
            WatchedAttributes.SetBool("canFallSideways", canFallSideways);
            WatchedAttributes.SetFloat("dustIntensity", dustIntensity);
            if (fallSound != null)
            {
                WatchedAttributes.SetString("fallSound", fallSound.ToShortString());
            }

            this.Code = new AssetLocation("blockfalling");
            this.blockCode = block.Code;
            this.removedBlockentity = blockEntity;
            this.initialPos = initialPos.Copy(); // Must copy — the external object may change

            // Center horizontally; slight downward offset removes z-fighting with the surface
            Pos.SetPos(initialPos);
            Pos.X += 0.5;
            Pos.Y -= 0.01;
            Pos.Z += 0.5;
        }


        public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
        {
            // Serialize the BlockEntity before the block is removed, to preserve its data
            if (removedBlockentity != null)
            {
                this.blockEntityAttributes = new TreeAttribute();
                removedBlockentity.ToTreeAttributes(blockEntityAttributes);
                blockEntityClass = api.World.ClassRegistry.GetBlockEntityClass(removedBlockentity.GetType());
            }

            // Large SimulationRange — entity ticks regardless of player distance
            SimulationRange = 1500000;

            base.Initialize(properties, api, InChunkIndex3d);

            try
            {
                // Cache drops before the block is removed — it won't be findable by position afterward
                drops = Block.GetDrops(api.World, initialPos, null);
            }
            catch (Exception)
            {
                drops = null;
                api.Logger.Warning("Falling block entity could not properly initialise its drops during chunk loading, as original block is no longer at " + initialPos);
            }

            lightHsv = Block.GetLightHsv(World.BlockAccessor, initialPos);

            // The first drop is used to determine dust particle color
            if (drops != null && drops.Length > 0)
            {
                stackForParticleColor = drops[0];
            }
            else
            {
                stackForParticleColor = new ItemStack(Block);
            }

            // Start the fall sound only on the client and only if a slot is free (limit: 100)
            if (api.Side == EnumAppSide.Client && fallSound != null && fallingNow.Count < 100)
            {
                fallingNow.Add(EntityId);
                ICoreClientAPI capi = api as ICoreClientAPI;
                sound = capi.World.LoadSound(new SoundParams()
                {
                    Location = fallSound.WithPathPrefixOnce("sounds/").WithPathAppendixOnce(".ogg"),
                    Position = new Vec3f((float)Pos.X, (float)Pos.Y, (float)Pos.Z),
                    Range = 32,
                    Pitch = 0.8f + (float)capi.World.Rand.NextDouble() * 0.3f,
                    Volume = 1,
                    SoundType = EnumSoundType.Ambient

                });
                sound.Start();
                soundStartDelay = 0.05f + (float)capi.World.Rand.NextDouble() / 3f;
            }

            // Re-read from WatchedAttributes — the parameterized constructor is not called on the client
            canFallSideways = WatchedAttributes.GetBool("canFallSideways");
            dustIntensity = WatchedAttributes.GetFloat("dustIntensity");

            if (WatchedAttributes.HasAttribute("fallSound"))
            {
                fallSound = new AssetLocation(WatchedAttributes.GetString("fallSound"));
            }

            if (api.World.Side == EnumAppSide.Client)
            {
                // Register with the particle system — dust will start spawning for us from this point
                particleSys = api.ModLoader.GetModSystem<FallingBlockParticlesModSystem>();
                particleSys.Register(this);
            }

            // Shuffle lateral direction order — deterministic by EntityId so server and client match
            RandomizeFallingDirectionsOrder();

            if (DoRemoveBlock)
            {
                // Now safe to remove the block from the world — drops and BlockEntity are already saved.
                // On the server, client notification is deferred until UpdateBlock(true, ...) is called.
                World.BlockAccessor.SetBlock(0, initialPos);
            }
        }


        /// <summary>
        /// Main entity tick. Updates sound, runs physics, checks for ground impact.
        /// Physics are skipped for the first 2 ticks — gives time to render the block as an entity.
        /// </summary>
        public override void OnGameTick(float dt)
        {
            World.FrameProfiler.Enter("entity-tick-unsstablefalling");

            // Sound start delay: Start() is called slightly after loading
            if (soundStartDelay > 0)
            {
                soundStartDelay -= dt;
                if (soundStartDelay <= 0)
                {
                    sound.Start();
                }
            }
            // Keep the sound source position in sync with the block
            if (sound != null)
            {
                sound.SetPosition((float)Pos.X, (float)Pos.Y, (float)Pos.Z);
            }

            // lingerTicks: block has already landed, wait briefly before dying (for smoothness)
            if (lingerTicks > 0)
            {
                lingerTicks--;
                if (lingerTicks == 0)
                {
                    // Fade out the sound before destroying the entity
                    if (Api.Side == EnumAppSide.Client && sound != null)
                    {
                        sound.FadeOut(3f, (s) => { s.Dispose(); });
                    }
                    Die();
                }

                return;
            }

            World.FrameProfiler.Mark("entity-tick-unsstablefalling-sound(etc)");

            ticksAlive++;
            // On the client, remove the block immediately (tick 1) or we miss the retessellation event.
            // On the server, wait 2 ticks for stability.
            if (ticksAlive >= 2 || Api.World.Side == EnumAppSide.Client)
            {
                if (!InitialBlockRemoved)
                {
                    InitialBlockRemoved = true;
                    UpdateBlock(true, initialPos);
                }

                // Tick behaviors (including physics) only after the delay
                foreach (EntityBehavior behavior in SidedProperties.Behaviors)
                {
                    behavior.OnGameTick(dt);
                }
                World.FrameProfiler.Mark("entity-tick-unsstablefalling-physics(etc)");
            }

            // Periodically check whether all players have moved away from this block.
            // If so, simulate instantly and destroy the entity —
            // otherwise it would hang around until someone comes back.
            if (Api.Side == EnumAppSide.Server && Api.World.ElapsedMilliseconds - lastPlayerCheckMs > PlayerCheckIntervalMs)
            {
                lastPlayerCheckMs = Api.World.ElapsedMilliseconds;
                if (!IsPlayerNearby())
                {
                    FallNow();
                    return;
                }
            }

            // Push and damage nearby entities — once per 0.2 s
            pushaccum += dt;
            fallMotion.X *= 0.99f;
            fallMotion.Z *= 0.99f;
            if (pushaccum > 0.2f)
            {
                pushaccum = 0;
                if (!Collided)
                {
                    Entity[] entities;
                    if (Api.Side == EnumAppSide.Server)
                    {
                        // On the server, hit all non-falling creatures
                        entities = World.GetEntitiesAround(this.Pos.XYZ, 1.1f, 1.1f, (e) => !(e is EntityBlockFalling));
                    }
                    else
                    {
                        // On the client, only players (for predictive push)
                        entities = World.GetEntitiesAround(this.Pos.XYZ, 1.1f, 1.1f, (e) => (e.IsCreature && e is EntityPlayer));
                    }

                    bool didhit = false;
                    foreach (var entity in entities)
                    {
                        if (Api.Side == EnumAppSide.Server)
                        {
                            // Crushing damage is proportional to vertical speed
                            bool nowhit = entity.ReceiveDamage(new DamageSource() { Source = EnumDamageSource.Block, Type = EnumDamageType.Crushing, SourceBlock = Block, SourcePos = Pos.XYZ }, 10 * (float)Math.Abs(Pos.Motion.Y) * impactDamageMul);
                            if (nowhit && !didhit)
                            {
                                didhit = nowhit;
                                Api.World.PlaySoundAt(this.Block.Sounds.Break, entity);
                            }
                        }

                        // Push in the horizontal direction the block is moving
                        entity.Pos.Motion.Add(fallMotion.X / 10f, 0, fallMotion.Z / 10f);
                    }
                }
            }

            World.FrameProfiler.Mark("entity-tick-unsstablefalling-finalizemotion");

            // Occasional neighbor update during fall — lets adjacent blocks react
            if (Api.Side == EnumAppSide.Server && !Collided && World.Rand.NextDouble() < 0.01)
            {
                World.BlockAccessor.TriggerNeighbourBlockUpdate(Pos.AsBlockPos);
                World.FrameProfiler.Mark("entity-tick-unsstablefalling-neighborstrigger");
            }

            // Block has stopped vertically — time to handle landing
            if (CollidedVertically && Pos.Motion.Length() < 1E-3f)
            {
                OnFallToGround(0);
                World.FrameProfiler.Mark("entity-tick-unsstablefalling-falltoground");
            }

            World.FrameProfiler.Leave();
        }


        // Timestamp of the last player proximity check (in game milliseconds)
        private long lastPlayerCheckMs;
        // Interval between player proximity checks
        private const int PlayerCheckIntervalMs = 2000;

        /// <summary>
        /// Checks whether at least one online player is within activeRange of the block.
        /// </summary>
        private bool IsPlayerNearby()
        {
            int activeRange = (Api as ICoreServerAPI)?.World.DefaultEntityTrackingRange * GlobalConstants.ChunkSize ?? 128;
            Vec3d pos = Pos.XYZ;
            EntityPlayer eplr;

            foreach (IPlayer player in Api.World.AllOnlinePlayers)
            {
                eplr = player.Entity;
                if (eplr != null && eplr.Pos.InRangeOf(pos, activeRange * activeRange, activeRange))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Forcefully completes the fall: instantly simulates the trajectory and destroys the entity.
        /// Called when all players leave the area — no point keeping the entity alive.
        /// </summary>
        private void FallNow()
        {
            if (fallHandled)
                return;
            fallHandled = true;

            SimulateInstantFall(Api.World, Block, removedBlockentity, initialPos, drops, false);
            Die(EnumDespawnReason.Removed);
        }

        /// <summary>
        /// Drops the block's items and BlockEntity contents at the center of the given position.
        /// </summary>
        public static void SpawnDrops(IWorldAccessor world, BlockPos pos, ItemStack[] drops, BlockEntity be)
        {
            var dpos = pos.ToVec3d().Add(0.5, 0.5, 0.5);
            if (drops != null)
                foreach (var drop in drops)
                    world.SpawnItemEntity(drop, dpos);
            // If the block had a container with inventory — drop its contents too
            if (be is IBlockEntityContainer bec)
                bec.DropContents(dpos);
        }


        /// <summary>
        /// Instant fall simulation without creating an entity.
        /// Finds the first free position below and places the block there,
        /// or drops items if no valid position exists.
        /// Used for blocks outside the player's visibility range.
        /// </summary>
        public static void SimulateInstantFall(
            IWorldAccessor world,
            Block block,
            BlockEntity be,
            BlockPos startPos,
            ItemStack[] drops,
            bool doRemoveBlock)
        {
            if (doRemoveBlock)
            {
                // Validity check: the block may have changed while the request was waiting
                if (world.BlockAccessor.GetBlock(startPos) != block)
                    return;
                world.BlockAccessor.SetBlock(0, startPos);
            }

            BlockPos finalPos = startPos.Copy();

            // Serialize the BlockEntity once for use in CanAcceptFallOnto / OnFallOnto
            TreeAttribute beTree = null;
            if (be != null)
            {
                beTree = new TreeAttribute();
                be.ToTreeAttributes(beTree);
            }

            int worldHeight = world.BlockAccessor.MapSizeY;

            // Descend while blocks below are passable (water, air, foliage, etc.)
            for (int i = 0; i < worldHeight; i++)
            {
                BlockPos belowPos = finalPos.DownCopy();
                Block belowBlock = world.BlockAccessor.GetMostSolidBlock(belowPos);

                // Special block handler (e.g. loose soil, hopper, etc.)
                if (belowBlock.CanAcceptFallOnto(world, belowPos, block, beTree))
                {
                    belowBlock.OnFallOnto(world, belowPos, block, beTree);
                    return;
                }

                if (belowBlock.Replaceable >= 6000 || belowBlock.IsLiquid())
                    finalPos = belowPos;  // Can pass through
                else
                    break;               // Hit a solid block
            }

            // Validate the final position: needs solid support below and free space above
            Block targetBlock = world.BlockAccessor.GetBlock(finalPos);
            Block supportBlock = world.BlockAccessor.GetMostSolidBlock(finalPos.DownCopy());

            if (supportBlock.Replaceable < 6000 && !supportBlock.IsLiquid() &&
                (targetBlock.IsLiquid() || targetBlock.Replaceable >= 6000))
            {
                // Conditions met — place the block at the final position
                world.BlockAccessor.SetBlock(block.BlockId, finalPos);

                // Restore BlockEntity state at the new position if it existed
                if (be != null)
                {
                    BlockEntity newBe = world.BlockAccessor.GetBlockEntity(finalPos);
                    if (newBe != null)
                    {
                        var tree = new TreeAttribute();
                        be.ToTreeAttributes(tree);
                        tree.SetInt("posx", finalPos.X);
                        tree.SetInt("posy", finalPos.InternalY);
                        tree.SetInt("posz", finalPos.Z);
                        newBe.FromTreeAttributes(tree, world);
                    }
                }

                return;
            }

            // Nowhere to land — drop items
            SpawnDrops(world, finalPos, drops, be);
        }


        public override void OnEntityDespawn(EntityDespawnData despawn)
        {
            base.OnEntityDespawn(despawn);

            if (Api.World.Side == EnumAppSide.Client)
            {
                // Release the sound slot and unregister from the particle system
                fallingNow.Remove(EntityId);
                particleSys.Unregister(this);
            }
        }


        /// <summary>
        /// Updates the block in the world: when remove=true, marks it dirty (block already removed);
        /// when remove=false, places the block at the new position and restores the BlockEntity.
        /// </summary>
        private void UpdateBlock(bool remove, BlockPos pos)
        {
            if (remove)
            {
                if (DoRemoveBlock)
                {
                    // Mark the chunk for redraw; enable entity rendering once retessellation completes
                    World.BlockAccessor.MarkBlockDirty(pos, () => OnChunkRetesselated(true));
                }
                else
                {
                    OnChunkRetesselated(true);
                }
            }
            else
            {
                var lbock = World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
                if (lbock.Id == 0 || Block.BlockMaterial != EnumBlockMaterial.Snow)
                {
                    World.BlockAccessor.SetBlock(Block.BlockId, pos);
                    // After redraw, disable entity rendering — the block is now static
                    World.BlockAccessor.MarkBlockDirty(pos, () => OnChunkRetesselated(false));
                }
                else
                {
                    OnChunkRetesselated(true);
                }

                // Restore BlockEntity data at the new position
                if (blockEntityAttributes != null)
                {
                    BlockEntity be = World.BlockAccessor.GetBlockEntity(pos);

                    blockEntityAttributes.SetInt("posx", pos.X);
                    blockEntityAttributes.SetInt("posy", pos.Y);
                    blockEntityAttributes.SetInt("posz", pos.Z);

                    if (be != null)
                    {
                        be.FromTreeAttributes(blockEntityAttributes, World);
                    }
                }
            }

            World.BlockAccessor.TriggerNeighbourBlockUpdate(pos);
        }


        /// <summary>
        /// Enables or disables entity rendering depending on chunk tessellation state.
        /// While the chunk is being redrawn — render as entity; afterward — hide.
        /// </summary>
        private void OnChunkRetesselated(bool on)
        {
            EntityBlockFallingRenderer renderer = (Properties.Client.Renderer as EntityBlockFallingRenderer);
            if (renderer != null) renderer.DoRender = on;
        }

        /// <summary>
        /// Shuffles the order of lateral directions for slide variety.
        /// Deterministic by EntityId — server and client produce the same order.
        /// </summary>
        private void RandomizeFallingDirectionsOrder()
        {
            for (int i = fallDirections.Count - 1; i > 0; i--)
            {
                int swapIndex = GameMath.MurmurHash3Mod(EntityId.GetHashCode(), i, i, fallDirections.Count);
                int temp = fallDirections[i];
                fallDirections[i] = fallDirections[swapIndex];
                fallDirections[swapIndex] = temp;
            }
            lastFallDirection = fallDirections[3];
        }

        /// <summary>
        /// Called when the block touches the ground (vertical velocity ≈ 0).
        /// Determines the final resting position.
        /// </summary>
        public override void OnFallToGround(double motionY)
        {
            if (fallHandled) return;

            BlockPos pos = Pos.AsBlockPos;
            BlockPos finalPos = Pos.AsBlockPos;
            Block block = null;

            if (Api.Side == EnumAppSide.Server)
            {
                block = World.BlockAccessor.GetMostSolidBlock(finalPos);

                if (block.CanAcceptFallOnto(World, finalPos, Block, blockEntityAttributes))
                {
                    Api.Event.EnqueueMainThreadTask(() =>
                    {
                        block.OnFallOnto(World, finalPos, Block, blockEntityAttributes);
                    }, "BlockFalling-OnFallOnto");

                    lingerTicks = 3;
                    fallHandled = true;
                    return;
                }
            }

            // Try to slide sideways if the ground is uneven
            if (canFallSideways)
            {
                Block nblock;
                foreach (int i in fallDirections)
                {
                    BlockFacing facing = BlockFacing.ALLFACES[i];
                    // Don't slide back in the direction we just came from
                    if (facing == BlockFacing.NORTH && lastFallDirection == BlockFacing.SOUTH.Index) continue;
                    if (facing == BlockFacing.WEST && lastFallDirection == BlockFacing.EAST.Index) continue;
                    if (facing == BlockFacing.SOUTH && lastFallDirection == BlockFacing.NORTH.Index) continue;
                    if (facing == BlockFacing.EAST && lastFallDirection == BlockFacing.WEST.Index) continue;

#pragma warning disable CS0618 // Type or member is obsolete - but it's correct here as we use pos.InternalY
                    // Is the neighbor at the same level passable?
                    nblock = World.BlockAccessor.GetMostSolidBlock(pos.X + facing.Normali.X, pos.InternalY + facing.Normali.Y, pos.Z + facing.Normali.Z);
                    if (nblock.Replaceable >= 6000)
                    {
                        // Is the neighbor one level below also passable? — if so, slide diagonally down
                        nblock = World.BlockAccessor.GetMostSolidBlock(pos.X + facing.Normali.X, pos.InternalY + facing.Normali.Y - 1, pos.Z + facing.Normali.Z);
#pragma warning restore CS0618
                        if (nblock.Replaceable >= 6000)
                        {
                            if (Api.Side == EnumAppSide.Server)
                            {
                                Pos.X += facing.Normali.X;
                                Pos.Y += facing.Normali.Y;
                                Pos.Z += facing.Normali.Z;
                            }

                            fallMotion.Set(facing.Normalf.X, 0, facing.Normalf.Z);
                            lastFallDirection = i;
                            return; // Continue falling in the lateral direction
                        }
                    }
                }
            }

            // Impact moment — signal for the particle system
            nowImpacted = true;

            if (Api.Side == EnumAppSide.Server)
            {
                bool updateBlock = (block.Id != 0 && Block.BlockMaterial == EnumBlockMaterial.Snow) || block.IsReplacableBy(Block);

                if (updateBlock)
                {
                    Api.Event.EnqueueMainThreadTask(() =>
                    {
                        if (block.Id != 0 && Block.BlockMaterial == EnumBlockMaterial.Snow)
                        {
                            // Snow: increase snow layer level instead of replacing the block
                            UpdateSnowLayer(finalPos, block);
                            (Api as ICoreServerAPI).Network.BroadcastEntityPacket(EntityId, packetIdMagicNumber);
                        }
                        else if (block.IsReplacableBy(Block))
                        {
                            // Remove the initial block if not already done (safety check)
                            if (!InitialBlockRemoved)
                            {
                                InitialBlockRemoved = true;
                                UpdateBlock(true, initialPos);
                            }

                            // Place the block at the final position and notify clients
                            UpdateBlock(false, finalPos);
                            (Api as ICoreServerAPI).Network.BroadcastEntityPacket(EntityId, packetIdMagicNumber);
                        }
                    }, "BlockFalling-consequences");
                }
                else
                {
                    if (block.Replaceable >= 6000)
                    {
                        // Final position is occupied by a soft block — drop items
                        DropItems(finalPos);
                    }
                    else
                    {
                        // Stuck — hop upward slightly and try again
                        Pos.Y += hopUpHeight;
                        hopUpHeight += 1;
                        if (hopUpHeight > 3) hopUpHeight = 1;
                        return;
                    }
                }

                // Damage all entities at the final position (crushing)
                if (impactDamageMul > 0)
                {
                    Entity[] entities = World.GetEntitiesInsideCuboid(finalPos, finalPos.AddCopy(1, 1, 1), (e) => !(e is EntityBlockFalling));
                    bool didhit = false;
                    foreach (var entity in entities)
                    {
                        bool nowhit = entity.ReceiveDamage(
                            new DamageSource() { Source = EnumDamageSource.Block, Type = EnumDamageType.Crushing, SourceBlock = Block, SourcePos = finalPos.ToVec3d() },
                            18 * (float)Math.Abs(motionY) * impactDamageMul
                        );

                        if (nowhit && !didhit)
                        {
                            didhit = nowhit;
                            Api.World.PlaySoundAt(this.Block.Sounds.Break, entity);
                        }
                    }
                }
            }

            // Let the entity linger a little longer so the client can play the impact animation
            lingerTicks = 50;
            fallHandled = true;
            hopUpHeight = 1;
        }

        /// <summary>
        /// Increases the snow layer level on the target block.
        /// </summary>
        private void UpdateSnowLayer(BlockPos finalPos, Block block)
        {
            Block snowblock = block.GetSnowCoveredVariant(finalPos, block.snowLevel + 1);
            if (snowblock != null && snowblock != block)
            {
                World.BlockAccessor.ExchangeBlock(snowblock.Id, finalPos);
            }
        }

        /// <summary>
        /// Receives a packet from the server: block has landed.
        /// </summary>
        public override void OnReceivedServerPacket(int packetid, byte[] data)
        {
            base.OnReceivedServerPacket(packetid, data);

            if (packetid == packetIdMagicNumber)
            {
                EntityBlockFallingRenderer renderer = (Properties.Client.Renderer as EntityBlockFallingRenderer);
                if (renderer != null)
                {
                    World.BlockAccessor.MarkBlockDirty(Pos.AsBlockPos, () => OnChunkRetesselated(false));
                }

                lingerTicks = 50;
                fallHandled = true;
                nowImpacted = true;
                // Stop spawning particles — the block is now static
                particleSys.Unregister(this);
            }
        }


        /// <summary>
        /// Drops the block's items and container contents (if any) at the given position.
        /// </summary>
        private void DropItems(BlockPos pos)
        {
            SpawnDrops(World, pos, drops, removedBlockentity);
        }


        /// <summary>
        /// The block that is falling. Looked up each time by blockCode.
        /// </summary>
        public Block Block
        {
            get { return World.BlockAccessor.GetBlock(blockCode); }
        }

        public override void ToBytes(BinaryWriter writer, bool forClient)
        {
            WatchedAttributes.SetFloat("maxSpawnHeightForParticles", maxSpawnHeightForParticles);

            base.ToBytes(writer, forClient);

            // Serialize the initial position and block code
            writer.Write(initialPos.X);
            writer.Write(initialPos.InternalY);
            writer.Write(initialPos.Z);
            writer.Write(blockCode.ToShortString());
            writer.Write(blockEntityAttributes == null);

            if (blockEntityAttributes != null)
            {
                blockEntityAttributes.ToBytes(writer);
                writer.Write(blockEntityClass);
            }

            writer.Write(DoRemoveBlock);
        }

        public override void FromBytes(BinaryReader reader, bool forClient)
        {
            base.FromBytes(reader, forClient);

            // Restore the initial position and block code
            initialPos = new BlockPos(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            blockCode = new AssetLocation(reader.ReadString());

            bool beIsNull = reader.ReadBoolean();
            if (!beIsNull)
            {
                blockEntityAttributes = new TreeAttribute();
                blockEntityAttributes.FromBytes(reader);
                blockEntityClass = reader.ReadString();
            }

            if (WatchedAttributes.HasAttribute("fallSound"))
            {
                fallSound = new AssetLocation(WatchedAttributes.GetString("fallSound"));
            }

            canFallSideways = WatchedAttributes.GetBool("canFallSideways");
            dustIntensity = WatchedAttributes.GetFloat("dustIntensity");
            maxSpawnHeightForParticles = WatchedAttributes.GetFloat("maxSpawnHeightForParticles");

            DoRemoveBlock = reader.ReadBoolean();
        }

        // Falling blocks do not take damage
        public override bool ShouldReceiveDamage(DamageSource damageSource, float damage)
        {
            return false;
        }

        // Whether the block can be interacted with
        public override bool IsInteractable
        {
            get { return false; }
        }
    }
}
