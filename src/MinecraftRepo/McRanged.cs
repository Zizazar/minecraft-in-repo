using UnityEngine;
using UnityEngine.Rendering;

namespace MinecraftRepo
{
    /// A Minecraft firework rocket with one small-ball star: shot from a crossbow it flies straight at constant speed,
    /// launched from a surface it climbs, accelerating. It bursts at the end of its flight or on hitting anything,
    /// hurting everything within 5 blocks that it can see (the shooter too). In multiplayer every PC flies its own copy
    /// (McNet Firework): the host's copy hurts enemies, and each player is hurt by their own copy (so their shield counts).
    internal class FireworkEntity : MonoBehaviour
    {
        /// Flight duration of the sold rockets ("Flight Duration: 2").
        public const int Flight = 2;
        /// Minecraft: 5 + 2 per star.
        const float Damage = 7f;

        Vector3 vel;
        bool angled, remote;
        PlayerAvatar shooter; // another player's rocket skips them and can hit the local player
        float age, lifetime;
        Color[] colors;
        Transform visual;

        /// Exploded (Destroy is deferred, and a received rocket is stepped several times at once).
        public bool Gone { get; private set; }

        // DyeColor firework colours (the bright ones)
        static readonly Color32[] Palette =
        {
            new Color32(0xEB, 0x88, 0x44, 255), new Color32(0xC3, 0x54, 0xCD, 255), new Color32(0x66, 0x89, 0xD3, 255),
            new Color32(0xDE, 0xCF, 0x2A, 255), new Color32(0x41, 0xCD, 0x34, 255), new Color32(0xD8, 0x81, 0x98, 255),
            new Color32(0x28, 0x76, 0x97, 255), new Color32(0x7B, 0x2F, 0xBE, 255), new Color32(0xB3, 0x31, 0x2C, 255),
            new Color32(0xF0, 0xF0, 0xF0, 255),
        };

        /// The local player's rocket, sent to the others. `angled`: shot from a crossbow with `vel`. Otherwise lifted off
        /// the ground (vel is ignored).
        public static void Launch(Vector3 pos, Vector3 vel, bool angled)
        {
            float B = McWorld.B;
            if (!angled) vel = new Vector3(Gauss() * 0.001f, 0.05f, Gauss() * 0.001f) * 20f * B;
            float lifetime = (10 * Flight + Random.Range(0, 6) + Random.Range(0, 7)) / 20f;
            var colorIds = new int[Random.value < 0.4f ? 2 : 1];
            for (int i = 0; i < colorIds.Length; i++) colorIds[i] = Random.Range(0, Palette.Length);
            Spawn(pos, vel, angled, lifetime, colorIds, null, false);
            McNet.SendFirework(pos, vel, angled, lifetime, colorIds);
        }

        /// Creates it on this PC only (Launch, or another player's rocket arriving).
        public static FireworkEntity Spawn(Vector3 pos, Vector3 vel, bool angled, float lifetime, int[] colorIds, PlayerAvatar shooter, bool remote)
        {
            float B = McWorld.B;
            var go = new GameObject("mc_firework_rocket");
            go.transform.position = pos;
            var holder = new GameObject("visual").transform;
            holder.SetParent(go.transform, false);
            var m = ModelUtil.SpriteModel(McAssets.FireworkRocket, B * 0.5f);
            m.transform.SetParent(holder, false);
            ModelUtil.SetShadows(m, ShadowCastingMode.Off);

            var f = go.AddComponent<FireworkEntity>();
            f.vel = vel;
            f.angled = angled;
            f.visual = holder;
            f.lifetime = lifetime;
            f.shooter = shooter;
            f.remote = remote;
            f.colors = new Color[Mathf.Max(1, colorIds.Length)];
            for (int i = 0; i < f.colors.Length; i++) f.colors[i] = Palette[i < colorIds.Length ? Mathf.Clamp(colorIds[i], 0, Palette.Length - 1) : 0];
            McAssets.Play("launch", pos, 1f, 1f, 48f);
            return f;
        }

        static float Gauss() => Mathf.Sqrt(-2f * Mathf.Log(Mathf.Max(1e-6f, Random.value))) * Mathf.Cos(2f * Mathf.PI * Random.value);

        void Update() => Step(Time.deltaTime);

        public void Step(float dt)
        {
            if (Gone) return;
            float B = McWorld.B;
            age += dt;
            if (!angled)
            {
                // per tick: horizontal x1.15, +0.04 blocks/tick upward
                float k = Mathf.Pow(1.15f, dt * 20f);
                vel.x *= k;
                vel.z *= k;
                vel.y += 0.04f * 20f * 20f * B * dt;
            }
            var pos = transform.position;
            var step = vel * dt;
            if (McWorld.CastProjectile(pos, step, out var hit, shooter, remote))
            {
                Explode(hit.Ray.point - step.normalized * 0.05f, hit.Enemy, hit.Player);
                return;
            }
            transform.position = pos + step;
            // the spark trail, one per tick, falling back along the flight
            if (Random.value < dt * 20f)
                Particles.Sprite(pos, new Vector3(Gauss() * 0.05f * 20f * B, -vel.y * 0.5f, Gauss() * 0.05f * 20f * B), Random.Range(0.5f, 0.9f),
                    B * 0.15f, Color.white, McAssets.Spark, 1.6f * B, 0.91f, 0f, true);
            if (age >= lifetime || pos.y < -300f) Explode(transform.position, null, null);
        }

        /// Minecraft draws the rocket as a flat item facing the camera; its nose here points where it flies on screen.
        void LateUpdate()
        {
            var cam = Camera.main;
            if (!cam || !visual) return;
            var f = cam.transform.forward;
            var up = Vector3.ProjectOnPlane(vel, f);
            visual.rotation = Quaternion.LookRotation(f, up.sqrMagnitude > 1e-6f ? up : cam.transform.up);
        }

        void Explode(Vector3 pos, Enemy direct, PlayerAvatar directPlayer)
        {
            Gone = true;
            Destroy(gameObject);
            float B = McWorld.B, r = 5f * B;
            Burst(pos, colors);
            var cam = Camera.main;
            bool far = cam && (cam.transform.position - pos).magnitude > 16f * B;
            McAssets.Play(far ? "blast_far" : "blast", pos, 1f, 0.95f + Random.value * 0.1f, 96f);
            McWorld.Flash(pos, r * 2f);

            // enemies live on the host: its copy hurts them
            bool master = SemiFunc.IsMasterClientOrSingleplayer();
            if (master)
            {
                if (EnemyDirector.instance) EnemyDirector.instance.SetInvestigate(pos, r * 3f);
                foreach (var e in FindObjectsOfType<Enemy>())
                {
                    if (!e) continue;
                    var c = McWorld.EnemyCenter(e);
                    float dmg = e == direct ? Damage : Falloff(pos, c, c, r);
                    if (dmg <= 0f) continue;
                    var dir = Dir(c - pos);
                    McWorld.HurtEnemy(e, dmg, dir, dir * 3f + Vector3.up * 1.5f, 0f);
                }
            }
            // each player is hurt by their own copy; the host stands in for players without the mod
            foreach (var pa in SemiFunc.PlayerGetAll())
            {
                if (!pa || pa.isDisabled || pa.deadSet || !pa.playerHealth) continue;
                if (!pa.isLocal && !(master && !McNet.HasMod(pa))) continue;
                var feet = pa.transform.position;
                float dmg = pa == directPlayer ? Damage : Falloff(pos, feet + Vector3.up * 0.9f, feet + Vector3.up * 0.1f, r);
                if (dmg <= 0f) continue;
                if (pa.isLocal && McPlayer.Instance && McPlayer.Instance.ShieldBlocks(pos))
                {
                    McPlayer.Instance.OnShieldBlock(pos, null, dmg);
                    continue;
                }
                int hp = Mathf.Max(1, Mathf.RoundToInt(dmg * Plugin.DamageScale.Value));
                if (pa.isLocal) pa.playerHealth.Hurt(hp, false);
                else pa.playerHealth.HurtOther(hp, Vector3.zero, false);
            }
        }

        /// Minecraft's firework damage: f * sqrt((5 - d) / 5) within 5 blocks, if the burst sees the target's
        /// middle or feet.
        static float Falloff(Vector3 pos, Vector3 mid, Vector3 low, float r)
        {
            float d = (mid - pos).magnitude;
            if (d > r) return 0f;
            bool seen = !Physics.Linecast(pos, low, McWorld.StaticMask, QueryTriggerInteraction.Ignore)
                || !Physics.Linecast(pos, mid, McWorld.StaticMask, QueryTriggerInteraction.Ignore);
            return seen ? Damage * Mathf.Sqrt((r - d) / r) : 0f;
        }

        static Vector3 Dir(Vector3 d) => d.sqrMagnitude < 1e-4f ? Vector3.up : d.normalized;

        /// The small-ball star: a flash, then a shell of coloured sparks at 0.25 blocks/tick that slow, sink and fade.
        static void Burst(Vector3 pos, Color[] colors)
        {
            float B = McWorld.B;
            var c0 = colors[0];
            Particles.Sprite(pos, Vector3.zero, 0.2f, 7f * B, new Color(c0.r, c0.g, c0.b, 0.6f), new[] { McAssets.FireworkFlash });
            for (int i = 0; i < 90; i++)
            {
                var v = Random.onUnitSphere * (0.25f + Gauss() * 0.01f) * 20f * B;
                Particles.Sprite(pos, v, Random.Range(2.4f, 3f), B * 0.22f, colors[Random.Range(0, colors.Length)], McAssets.Spark,
                    1.6f * B, 0.91f, 0f, true);
            }
        }
    }

    /// Minecraft's fishing bobber: cast it, it flies on its line and hooks the first enemy, player, R.E.P.O. object or
    /// dropped item it touches (or lands); using the rod again reels in, yanking what it hooked toward you. The yank is
    /// the host's (McWorld.PushEnemy/YankPlayer/PushObject); the others see the bobber through RemoteBobber.
    internal class BobberEntity : MonoBehaviour
    {
        public static BobberEntity Current;
        public ItemStack Rod;

        Vector3 vel;
        float groundTime;
        bool onGround, hooked;
        Enemy hookedEnemy;
        PlayerAvatar hookedPlayer;
        PhysGrabObject hookedObject;
        ItemEntity hookedItem;
        Vector3 hookLocal;
        Transform stuckTo;
        Vector3 stuckLocal;
        Transform visual;
        LineRenderer line;
        readonly Vector3[] points = new Vector3[17];
        float sendTimer;

        static Mesh quad;

        public static bool IsCastWith(ItemStack s) => Current && s != null && Current.Rod == s;

        public static void Cast(ItemStack rod, Transform cam)
        {
            if (Current) Current.Discard();
            float B = McWorld.B;
            var fwd = cam.forward;
            var flat = Vector3.ProjectOnPlane(fwd, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.ProjectOnPlane(cam.up, Vector3.up);
            flat.Normalize();
            // Minecraft: (look direction on the ground, tan(pitch) clamped to 5), scaled to 0.6 blocks/tick plus half of itself
            float h = Mathf.Max(1e-3f, new Vector2(fwd.x, fwd.z).magnitude);
            var v = flat + Vector3.up * Mathf.Clamp(fwd.y / h, -5f, 5f);
            v *= 0.6f / v.magnitude + 0.5f + (Random.value - Random.value) * 0.0103f;

            var go = MakeVisual("mc_fishing_bobber", out var sprite, out var lr);
            go.transform.position = cam.position + flat * 0.3f * B;
            var b = go.AddComponent<BobberEntity>();
            b.Rod = rod;
            b.vel = v * 20f * B;
            b.visual = sprite;
            b.line = lr;
            Current = b;
        }

        /// The hook sprite and its line (also another player's bobber).
        public static GameObject MakeVisual(string name, out Transform sprite, out LineRenderer line)
        {
            float B = McWorld.B;
            var go = new GameObject(name);
            var q = new GameObject("sprite");
            q.transform.SetParent(go.transform, false);
            q.transform.localScale = Vector3.one * B * 0.5f;
            q.AddComponent<MeshFilter>().sharedMesh = Quad;
            var mr = q.AddComponent<MeshRenderer>();
            mr.sharedMaterial = Mats.SpriteTex(McAssets.FishingHook);
            mr.shadowCastingMode = ShadowCastingMode.Off;
            sprite = q.transform;

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 17;
            lr.widthMultiplier = 0.012f;
            lr.numCapVertices = 0;
            lr.sharedMaterial = Mats.SpriteTex(McAssets.White);
            lr.startColor = lr.endColor = Color.black;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            line = lr;
            return go;
        }

        /// Minecraft's sagging line from the bobber to the hand: x and z linear, y = dy * (f^2 + f) / 2.
        public static void DrawLine(LineRenderer line, Vector3[] points, Vector3 bobber, Vector3 hand)
        {
            var start = bobber + Vector3.up * McWorld.B * 0.1f;
            var d = hand - start;
            for (int i = 0; i < points.Length; i++)
            {
                float f = i / (float)(points.Length - 1);
                points[i] = start + new Vector3(d.x * f, d.y * (f * f + f) * 0.5f, d.z * f);
            }
            line.SetPositions(points);
        }

        static Mesh Quad
        {
            get
            {
                if (quad) return quad;
                var mb = new MeshBuilder();
                mb.Quad(new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0),
                    new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1));
                return quad = mb.Build("mc_bobber_quad");
            }
        }

        void Update()
        {
            var p = McPlayer.Instance;
            float B = McWorld.B, dt = Time.deltaTime;
            // Minecraft reels the line in by itself when the rod leaves your hands or the bobber is over 32 blocks away
            if (!p || !p.Active || (p.Inventory.Held != Rod && p.Inventory.Offhand != Rod)
                || (transform.position - p.FeetPosition).sqrMagnitude > 32f * 32f * B * B)
            {
                Discard();
                return;
            }
            if (hooked) { FollowHooked(); return; }
            if (onGround)
            {
                if (stuckTo) transform.position = stuckTo.TransformPoint(stuckLocal);
                else { onGround = false; vel = Vector3.zero; }
                groundTime += dt;
                if (groundTime > 60f) Discard();
                return;
            }

            vel.y -= 0.03f * 20f * 20f * B * dt; // 0.03 blocks/tick^2
            vel *= Mathf.Pow(0.92f, dt * 20f);
            var pos = transform.position;
            var step = vel * dt;
            foreach (var it in ItemEntity.All)
                if (it && DistanceToSegment(it.transform.position + Vector3.up * 0.1f, pos, pos + step) < 0.25f)
                {
                    Hook();
                    hookedItem = it;
                    return;
                }
            if (McWorld.CastProjectile(pos, step, out var hit)) OnHit(hit);
            else transform.position = pos + step;
            if (pos.y < -300f) Discard();
        }

        void OnHit(McWorld.Hit hit)
        {
            var pgo = hit.Pgo;
            if (hit.Enemy) { Hook(); hookedEnemy = hit.Enemy; }
            else if (hit.Player) { Hook(); hookedPlayer = hit.Player; }
            else if (pgo && !hit.Block && pgo.rb && !pgo.rb.isKinematic)
            {
                Hook();
                hookedObject = pgo;
                hookLocal = pgo.transform.InverseTransformPoint(hit.Ray.point);
            }
            else
            {
                var n = hit.Ray.normal;
                var pos = hit.Ray.point + n * 0.05f;
                transform.position = pos;
                if (n.y > 0.5f)
                {
                    onGround = true;
                    stuckTo = hit.Ray.collider.transform;
                    stuckLocal = stuckTo.InverseTransformPoint(pos);
                }
                else vel = Vector3.ProjectOnPlane(vel, n) * 0.2f; // off a wall, down to the floor
                return;
            }
            McAssets.Play("bowhit", hit.Ray.point, 0.3f, 1.6f);
        }

        void Hook()
        {
            hooked = true;
            vel = Vector3.zero;
        }

        void FollowHooked()
        {
            Vector3? at = null;
            if (hookedEnemy) at = McWorld.EnemyCenter(hookedEnemy);
            else if (hookedPlayer && !hookedPlayer.isDisabled) at = hookedPlayer.transform.position + Vector3.up * 1.2f;
            else if (hookedObject && !Storage.IsStored(hookedObject)) at = hookedObject.transform.TransformPoint(hookLocal);
            else if (hookedItem) at = hookedItem.transform.position + Vector3.up * 0.1f;
            if (at == null)
            {
                // what it hooked is gone (killed, mined, picked up): the bobber drops
                hooked = false;
                hookedEnemy = null; hookedPlayer = null; hookedObject = null; hookedItem = null;
                return;
            }
            transform.position = at.Value;
        }

        /// Reels in toward `to`. Minecraft adds 0.1 x the gap per tick to what it pulls (2 x the gap in m/s); a little
        /// lift is added so R.E.P.O.'s heavy objects don't just drag along the floor. Returns the rod's durability cost.
        public int Retrieve(Vector3 to)
        {
            int wear = 0;
            if (hooked)
            {
                // the host moves enemies, players and objects: on a client these ask it to
                if (hookedEnemy)
                {
                    wear = 5;
                    McWorld.PushEnemy(hookedEnemy, Pull(McWorld.EnemyCenter(hookedEnemy), to, 14f), true);
                }
                else if (hookedPlayer)
                {
                    wear = 5;
                    McWorld.YankPlayer(hookedPlayer, Pull(hookedPlayer.transform.position + Vector3.up * 0.9f, to, 14f));
                }
                else if (hookedObject)
                {
                    wear = 3;
                    var o = hookedObject;
                    McWorld.PushObject(o, Pull(o.rb ? o.rb.worldCenterOfMass : o.transform.position, to, 12f));
                }
                else if (hookedItem)
                {
                    wear = 3;
                    hookedItem.PushSynced(Pull(hookedItem.transform.position, to, 12f));
                }
            }
            else if (onGround) wear = 2;
            Discard();
            return wear;
        }

        static Vector3 Pull(Vector3 from, Vector3 to, float max)
        {
            var d = to - from;
            var v = d * 2f + Vector3.up * Mathf.Min(4f, 0.3f * new Vector2(d.x, d.z).magnitude);
            return Vector3.ClampMagnitude(v, max);
        }

        static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (p - (a + ab * t)).magnitude;
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            var p = McPlayer.Instance;
            if (!cam || !p) return;
            if (visual) visual.rotation = cam.transform.rotation;

            // the line leaves the rod tip: in first person Minecraft takes a fixed point right of and below the screen
            // centre (0.525, -0.1 of the near plane), in third person the hand
            int k = p.Inventory.Held == Rod ? 1 : -1;
            Vector3 hand;
            var pa = PlayerAvatar.instance;
            if (SteveManager.ViewMode == 0 || !pa) hand = cam.ViewportToWorldPoint(new Vector3(0.5f + 0.2625f * k, 0.45f, 0.4f));
            else hand = pa.transform.position + Vector3.up * 1.3f + pa.transform.right * 0.35f * k + pa.transform.forward * 0.3f;

            DrawLine(line, points, transform.position, hand);

            // the others draw it from 15 positions a second
            if ((sendTimer -= Time.deltaTime) <= 0f)
            {
                sendTimer = 1f / 15f;
                McNet.SendBobber(transform.position, k == 1 ? 0 : 1);
            }
        }

        public void Discard()
        {
            if (Current == this) Current = null;
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
            McNet.SendBobberGone();
        }
    }

    /// Another player's bobber where they last sent it, with its line to their hand (third-person rod tip).
    internal class RemoteBobber : MonoBehaviour
    {
        static readonly System.Collections.Generic.Dictionary<int, RemoteBobber> all = new System.Collections.Generic.Dictionary<int, RemoteBobber>();

        int actor;
        PlayerAvatar owner;
        Transform sprite;
        LineRenderer line;
        Vector3 target;
        int hand;
        float lastSeen;
        readonly Vector3[] points = new Vector3[17];

        public static void Set(int actor, Vector3 pos, int hand)
        {
            if (!all.TryGetValue(actor, out var b) || !b)
            {
                var go = BobberEntity.MakeVisual("mc_fishing_bobber_remote", out var sprite, out var line);
                go.transform.position = pos;
                all[actor] = b = go.AddComponent<RemoteBobber>();
                b.actor = actor;
                b.sprite = sprite;
                b.line = line;
            }
            if (!b.owner) b.owner = McNet.AvatarOf(actor);
            b.target = pos;
            b.hand = hand;
            b.lastSeen = Time.time;
        }

        public static void Remove(int actor)
        {
            if (!all.TryGetValue(actor, out var b)) return;
            all.Remove(actor);
            if (b) Destroy(b.gameObject);
        }

        public static void Clear()
        {
            foreach (var b in all.Values) if (b) Destroy(b.gameObject);
            all.Clear();
        }

        void LateUpdate()
        {
            // sent unreliably: it goes when they stop sending (left, reeled in and the "gone" got lost)
            if (!owner || owner.isDisabled || Time.time - lastSeen > 1f) { Remove(actor); return; }
            transform.position = Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-20f * Time.deltaTime));
            var cam = Camera.main;
            if (cam) sprite.rotation = cam.transform.rotation;
            int k = hand == 0 ? 1 : -1;
            var t = owner.transform;
            DrawLine(t.position + Vector3.up * 1.3f + t.right * 0.35f * k + t.forward * 0.3f);
        }

        void DrawLine(Vector3 handPos) => BobberEntity.DrawLine(line, points, transform.position, handPos);
    }
}
