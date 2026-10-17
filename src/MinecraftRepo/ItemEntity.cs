using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MinecraftRepo
{
    /// A Minecraft dropped item: a small copy of the thing that bobs, spins, falls, and gets picked up
    /// when a player walks over it. The real PhysGrabObject stays parked by Storage meanwhile.
    /// Shared in multiplayer: one client creates it (Drop), and one client decides it (the host when it has the mod,
    /// otherwise its creator): it gives it to the nearest player who has room, merges stacks and despawns it. The others
    /// only show it and ask for it, so each drop ends up in exactly one inventory.
    internal class ItemEntity : MonoBehaviour
    {
        public static readonly List<ItemEntity> All = new List<ItemEntity>();

        public ItemStack Stack;
        public int NetId, Owner;
        Vector3 velocity;
        float age, pickupDelay, bobOffset, claimTimer;
        bool grounded, resting, taken;
        Transform visual;
        float flyT = -1f;
        Vector3 flyFrom;
        int flyTo;              // actor the item flies to once it's taken
        ItemStack flyStack;     // what that player gets (the deciding client's stack)
        readonly Dictionary<int, float> claims = new Dictionary<int, float>(); // actor -> last time they asked for it

        const float Size = 0.3f;
        const float Radius = 0.12f;
        static int groundMask = -1;

        /// Drops an item for every player. Every drop goes through here, once, on one client.
        public static ItemEntity Drop(ItemStack stack, Vector3 pos, Vector3 velocity, float pickupDelay)
        {
            var e = Spawn(stack, pos, velocity, pickupDelay);
            e.NetId = McNet.NewId();
            e.Owner = McNet.LocalActor;
            McNet.Register(e);
            McNet.SendItem(e, velocity, pickupDelay);
            return e;
        }

        /// Creates it on this PC only (Drop, or another player's drop arriving).
        public static ItemEntity Spawn(ItemStack stack, Vector3 pos, Vector3 velocity, float pickupDelay)
        {
            var go = new GameObject("mc_item_" + stack.Key);
            go.transform.position = pos;
            var e = go.AddComponent<ItemEntity>();
            e.Stack = stack;
            e.velocity = velocity;
            e.pickupDelay = pickupDelay;
            e.bobOffset = Random.value * Mathf.PI * 2f;
            var model = ModelUtil.HeldModel(stack, McItems.HeldLikeTool(stack) ? 0.4f : Size);
            model.transform.SetParent(go.transform, false);
            ModelUtil.SetShadows(model, ShadowCastingMode.On);
            e.visual = model.transform;
            foreach (var o in stack.Objects) Storage.Store(o);
            return e;
        }

        static int GroundMask()
        {
            if (groundMask != -1) return groundMask;
            groundMask = ~0;
            foreach (var n in new[] { "Player", "PlayerOnlyCollision", "PlayerVisuals", "Triggers", "TopLayer", "PhysGrabObjectHinge", "Enemy" })
            {
                int l = LayerMask.NameToLayer(n);
                if (l >= 0) groundMask &= ~(1 << l);
            }
            groundMask &= ~(1 << 31);
            return groundMask;
        }

        /// This client decides who gets it, merges and despawns.
        public bool Authority => McNet.Resolves(Owner);

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);
        void OnDestroy() => McNet.Forget(this);

        void Update()
        {
            float dt = Time.deltaTime;
            if (flyT >= 0f) { FlyToTaker(dt); return; }

            Stack.Objects.RemoveAll(o => o == null);
            if (Stack.Count == 0) { Destroy(gameObject); return; }

            age += dt;
            if (pickupDelay > 0) pickupDelay -= dt;
            // Minecraft despawns items after 5 minutes. Only Minecraft items do here: a valuable is real money.
            if (Stack.IsVirtual && age > 300f && Authority) { Remove(); return; }

            Physicsish(dt);

            // Minecraft: bob 0.1 blocks, turn 1 radian per second
            visual.localPosition = new Vector3(0, Size * 0.5f + 0.05f + Mathf.Sin(age * 2f + bobOffset) * 0.06f, 0);
            visual.localRotation = Quaternion.Euler(0, (age + bobOffset) * Mathf.Rad2Deg, 0);

            if (taken) return;
            TryPickup(dt);
            if (Authority) MergeNearby();
        }

        /// Gone for everyone (despawned, fell out of the level).
        void Remove()
        {
            McNet.SendItemGone(this);
            Destroy(gameObject);
        }

        void Physicsish(float dt)
        {
            var pos = transform.position;
            velocity.y -= 16f * dt;
            var step = velocity * dt;
            float dist = step.magnitude;
            if (dist > 0.0001f && Physics.SphereCast(pos + Vector3.up * Radius, Radius, step.normalized, out var hit, dist, GroundMask(), QueryTriggerInteraction.Ignore))
            {
                pos = pos + step.normalized * Mathf.Max(0, hit.distance - 0.01f);
                if (hit.normal.y > 0.5f) { grounded = true; velocity.y = 0; }
                velocity = Vector3.ProjectOnPlane(velocity, hit.normal) * 0.5f;
            }
            else
            {
                pos += step;
                grounded = false;
            }
            float friction = grounded ? Mathf.Pow(0.02f, dt) : Mathf.Pow(0.6f, dt);
            velocity.x *= friction;
            velocity.z *= friction;
            transform.position = pos;

            // where it comes to rest on the deciding client is where it lies for everyone
            bool rest = grounded && new Vector2(velocity.x, velocity.z).sqrMagnitude < 0.0025f;
            if (rest && !resting && Authority) McNet.SendItemMove(this);
            resting = rest;
            if (pos.y < -200f && Authority) Remove();
        }

        /// Snaps it to where it rests on the deciding client.
        public void SetRest(Vector3 pos)
        {
            if (taken) return;
            transform.position = pos;
            velocity = Vector3.zero;
            grounded = resting = true;
        }

        bool InRange(Vector3 feet, float slack)
        {
            var d = transform.position - feet;
            return new Vector2(d.x, d.z).magnitude <= 1.3f + slack && d.y >= -0.6f - slack && d.y <= 2.2f + slack;
        }

        /// Minecraft gives an item to the nearest player that can hold it. Players ask the deciding client while they
        /// stand on it; it compares them with its own player and gives it to the nearest.
        void TryPickup(float dt)
        {
            if (pickupDelay > 0) return;
            var p = McPlayer.Instance;
            bool meIn = p != null && p.Active && InRange(p.FeetPosition, 0f) && p.Inventory.HasRoomFor(Stack);
            if (!Authority)
            {
                if (meIn && (claimTimer -= dt) <= 0f) { claimTimer = 0.2f; McNet.SendItemClaim(this); }
                return;
            }

            int best = McNet.Nobody;
            float bestD = float.MaxValue;
            if (meIn) { best = McNet.LocalActor; bestD = (p.FeetPosition - transform.position).sqrMagnitude; }
            foreach (var kv in claims)
            {
                if (Time.time - kv.Value > 0.5f) continue;
                var pa = McNet.AvatarOf(kv.Key);
                // their claim is checked against where this client sees them, with some slack for lag
                if (!pa || pa.isDisabled || pa.deadSet || !InRange(pa.transform.position, 1.5f)) continue;
                float d = (pa.transform.position - transform.position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = kv.Key; }
            }
            if (best == McNet.Nobody) return;
            McNet.SendItemPickup(this, best);
            Take(best, Stack);
        }

        public void AddClaim(int actor) { if (!taken) claims[actor] = Time.time; }

        /// Given to `actor`: it flies to them; their client gets `stack`.
        public void Take(int actor, ItemStack stack)
        {
            taken = true;
            flyTo = actor;
            flyStack = stack;
            flyT = 0f;
            flyFrom = transform.position;
        }

        void FlyToTaker(float dt)
        {
            bool mine = flyTo == McNet.LocalActor;
            Vector3 target;
            if (mine) target = McPlayer.Instance ? McPlayer.Instance.FeetPosition + Vector3.up * 0.8f : transform.position;
            else
            {
                var pa = McNet.AvatarOf(flyTo);
                if (!pa) { Destroy(gameObject); return; } // they left; it was theirs
                target = pa.transform.position + Vector3.up * 0.8f;
            }
            flyT += dt / 0.15f;
            transform.position = Vector3.Lerp(flyFrom, target, flyT * flyT);
            if (flyT < 1f) return;
            McAssets.Play("pop", target, 0.2f, ((Random.value - Random.value) * 0.7f + 1f) * 2f);
            if (mine) Collect(flyStack, flyFrom);
            Destroy(gameObject);
        }

        /// Puts a picked-up stack into the local inventory; if it filled up meanwhile, what's left drops again.
        public static void Collect(ItemStack stack, Vector3 dropAt)
        {
            var p = McPlayer.Instance;
            if (p != null && p.Inventory.Add(stack)) return;
            if (stack.Count > 0) Drop(stack, dropAt, Vector3.zero, 1f);
        }

        /// Explosions push dropped items (every client runs the explosion, so this stays local).
        public void Push(Vector3 dv)
        {
            if (flyT >= 0f) return;
            velocity += dv;
            grounded = resting = false;
        }

        /// A push only this client sees happen (the fishing rod's reel), sent to the others.
        public void PushSynced(Vector3 dv)
        {
            Push(dv);
            McNet.SendItemPush(this, dv);
        }

        void MergeNearby()
        {
            if (Stack.MaxStack <= 1 || age < 0.5f || Time.frameCount % 10 != 0) return;
            foreach (var other in All)
            {
                if (other == this || !Stack.CanMerge(other.Stack) || other.flyT >= 0 || other.taken || !other.Authority) continue;
                if ((other.transform.position - transform.position).sqrMagnitude > 0.5f * 0.5f) continue;
                if (Stack.Count + other.Stack.Count > Stack.MaxStack) continue;
                Stack.Absorb(other.Stack, other.Stack.Count);
                McNet.SendItemMerge(this, other);
                Destroy(other.gameObject);
                break;
            }
        }
    }

    /// Minecraft-style particles drawn with Graphics.DrawMesh: block-break and crit cubes, plus camera-facing
    /// sprites (explosion, flame, smoke, portal) animated through their Minecraft frames.
    internal class Particles : MonoBehaviour
    {
        struct P { public Vector3 pos, vel; public float life, size; public Color color; public bool gravity; }
        struct S
        {
            public Vector3 pos, vel;
            public float age, life, size, gravity, drag, delay;
            public Color color;
            public Texture2D[] frames;
            public bool shrink;
        }
        static Particles inst;
        readonly List<P> ps = new List<P>();
        readonly List<S> sprites = new List<S>();
        MaterialPropertyBlock mpb;
        static Mesh quad;
        static Texture2D[] smokeFrames;

        static Mesh Quad
        {
            get
            {
                if (quad) return quad;
                var mb = new MeshBuilder();
                mb.Quad(new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0),
                    new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1));
                return quad = mb.Build("mc_particle_quad");
            }
        }

        /// Minecraft's smoke runs generic_7 down to generic_0 as it ages.
        static Texture2D[] Smoke
        {
            get
            {
                if (smokeFrames != null) return smokeFrames;
                smokeFrames = (Texture2D[])McAssets.Generic.Clone();
                System.Array.Reverse(smokeFrames);
                return smokeFrames;
            }
        }

        public static void Sprite(Vector3 pos, Vector3 vel, float life, float size, Color color, Texture2D[] frames,
            float gravity = 0f, float drag = 0.9f, float delay = 0f, bool shrink = false)
        {
            if (frames == null || frames.Length == 0 || I.sprites.Count > 3000) return;
            I.sprites.Add(new S { pos = pos, vel = vel, life = life, size = size, color = color, frames = frames, gravity = gravity, drag = drag, delay = delay, shrink = shrink });
        }

        /// Minecraft's explosion_emitter: a burst of big animated puffs spread over a few ticks, plus smoke.
        public static void Explosion(Vector3 center, float radius)
        {
            for (int i = 0; i < 40; i++)
            {
                var off = new Vector3(Random.value - Random.value, Random.value - Random.value, Random.value - Random.value) * radius;
                float g = Random.Range(0.4f, 1f);
                Sprite(center + off, Vector3.zero, Random.Range(0.3f, 0.5f), radius * 0.5f * (1f - Random.value * 0.5f),
                    new Color(g, g, g), McAssets.Explosion, delay: Random.Range(0f, 0.4f));
            }
            for (int i = 0; i < 24; i++)
            {
                var dir = Random.onUnitSphere;
                Sprite(center + dir * radius * 0.3f, dir * Random.Range(0.5f, 2f) + Vector3.up * 0.5f, Random.Range(0.8f, 1.6f),
                    radius * 0.18f, new Color(0.3f, 0.3f, 0.3f, 0.9f), Smoke, -0.3f, 0.6f, Random.Range(0f, 0.3f));
            }
        }

        public static void SmokePuff(Vector3 pos, float size, int count, Color color)
        {
            for (int i = 0; i < count; i++)
                Sprite(pos + Random.insideUnitSphere * size * 0.5f, Random.insideUnitSphere * 0.2f + Vector3.up * 0.3f, Random.Range(0.6f, 1.2f), size, color, Smoke, -0.2f, 0.5f);
        }

        static Texture2D[] flameFrames;

        public static void Flame(Vector3 pos, float size)
        {
            if (flameFrames == null) flameFrames = new[] { McAssets.Flame };
            Sprite(pos, new Vector3(Random.Range(-0.02f, 0.02f), Random.Range(0.02f, 0.08f), Random.Range(-0.02f, 0.02f)), Random.Range(0.4f, 0.9f), size,
                Color.white, flameFrames, 0f, 0.96f, 0f, true);
        }

        /// Ender pearl landing: purple sparkles flying outward.
        public static void Portal(Vector3 pos, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float f = Random.value * 0.6f + 0.4f;
                var tex = new[] { McAssets.Generic[Random.Range(0, McAssets.Generic.Length)] };
                Sprite(pos + Random.insideUnitSphere * 0.3f, Random.insideUnitSphere * 2f, Random.Range(0.4f, 0.9f), Random.Range(0.06f, 0.12f),
                    new Color(f * 0.9f, f * 0.3f, f), tex, 0f, 0.4f, 0f, true);
            }
        }

        static Particles I
        {
            get
            {
                if (inst) return inst;
                var go = new GameObject("mc_particles");
                DontDestroyOnLoad(go);
                return inst = go.AddComponent<Particles>();
            }
        }

        public static void Burst(Vector3 center, Vector3 extents, Texture2D colors, int count, float speed)
        {
            for (int i = 0; i < count; i++)
            {
                var off = Vector3.Scale(Random.insideUnitSphere, extents);
                I.ps.Add(new P
                {
                    pos = center + off,
                    vel = off.normalized * Random.Range(0.3f, 1f) * speed + Vector3.up * Random.Range(0.5f, 2f),
                    life = Random.Range(0.4f, 1.1f),
                    size = Random.Range(0.04f, 0.08f),
                    color = SampleColor(colors),
                    gravity = true,
                });
            }
        }

        public static void Crit(Vector3 center, int count)
        {
            for (int i = 0; i < count; i++)
                I.ps.Add(new P
                {
                    pos = center + Random.insideUnitSphere * 0.3f,
                    vel = Random.onUnitSphere * Random.Range(1.5f, 3f) + Vector3.up,
                    life = Random.Range(0.3f, 0.7f),
                    size = Random.Range(0.03f, 0.05f),
                    color = new Color(0.85f, 0.85f, 0.85f),
                    gravity = false,
                });
        }

        static Color SampleColor(Texture2D t)
        {
            if (t == null || !t.isReadable) return Color.gray;
            for (int tries = 0; tries < 10; tries++)
            {
                var c = t.GetPixel(Random.Range(0, t.width), Random.Range(0, t.height));
                if (c.a > 0.5f) return c * 0.85f;
            }
            return Color.gray;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            for (int i = sprites.Count - 1; i >= 0; i--)
            {
                var p = sprites[i];
                if (p.delay > 0f) { p.delay -= dt; sprites[i] = p; continue; }
                p.age += dt;
                if (p.age >= p.life) { sprites.RemoveAt(i); continue; }
                p.vel.y -= p.gravity * dt;
                p.vel *= Mathf.Pow(p.drag, dt * 20f);
                p.pos += p.vel * dt;
                sprites[i] = p;
            }
            for (int i = ps.Count - 1; i >= 0; i--)
            {
                var p = ps[i];
                p.life -= dt;
                if (p.life <= 0) { ps.RemoveAt(i); continue; }
                if (p.gravity) p.vel.y -= 9f * dt;
                p.vel *= Mathf.Pow(0.3f, dt);
                p.pos += p.vel * dt;
                ps[i] = p;
            }
        }

        void LateUpdate()
        {
            if (mpb == null) mpb = new MaterialPropertyBlock();
            var cam = Camera.main;
            if (sprites.Count > 0 && cam)
            {
                var rot = cam.transform.rotation;
                var quadMesh = Quad;
                foreach (var p in sprites)
                {
                    if (p.delay > 0f) continue;
                    float t = p.age / p.life;
                    var tex = p.frames[Mathf.Min(p.frames.Length - 1, (int)(t * p.frames.Length))];
                    float size = p.shrink ? p.size * (1f - t * t * 0.5f) : p.size;
                    mpb.Clear();
                    mpb.SetColor("_Color", p.color);
                    Graphics.DrawMesh(quadMesh, Matrix4x4.TRS(p.pos, rot, Vector3.one * size), Mats.SpriteTex(tex), 0, null, 0, mpb, false, false);
                }
            }
            if (ps.Count == 0) return;
            mpb.Clear();
            var mesh = ModelUtil.Cube;
            var mat = Mats.LitWhite;
            foreach (var p in ps)
            {
                mpb.SetColor("_Color", p.color);
                Graphics.DrawMesh(mesh, Matrix4x4.TRS(p.pos, Quaternion.identity, Vector3.one * p.size), mat, 0, null, 0, mpb, false, false);
            }
        }
    }
}
