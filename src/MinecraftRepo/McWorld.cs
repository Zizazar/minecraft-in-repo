using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MinecraftRepo
{
    /// World-side Minecraft rules that aren't tied to the player's hands: damage helpers, burning, lights
    /// (placed torches and fire, the held torch and the flashlight item), and placement checks.
    /// Entities live as plain GameObjects in the level scene, so a level change clears them.
    internal class McWorld : MonoBehaviour
    {
        public static McWorld Instance;

        /// Metres per Minecraft block.
        public static float B => Mathf.Max(0.1f, Plugin.BlockSize.Value);

        void Awake() => Instance = this;

        void Update()
        {
            Burning.Tick(Time.deltaTime);
            CullLights();
        }

        void LateUpdate()
        {
            UpdateHeldLights();
            Burning.Draw();
        }

        // ---------- layers ----------

        static int staticMask = -1, placeMask = -1, groundMask = -1;

        /// Level geometry only: what blocks an explosion's rays.
        public static int StaticMask => staticMask != -1 ? staticMask : staticMask = LayerMask.GetMask("Default", "StaticGrabObject");

        /// What a placed block may not overlap.
        public static int PlaceMask => placeMask != -1 ? placeMask
            : placeMask = LayerMask.GetMask("Default", "StaticGrabObject", "PhysGrabObject", "PhysGrabObjectCart", "PhysGrabObjectHinge", "Player", "Enemy");

        /// Floors for fire and teleport landings.
        public static int GroundMask => groundMask != -1 ? groundMask : groundMask = LayerMask.GetMask("Default", "StaticGrabObject", "PhysGrabObject", "PhysGrabObjectCart");

        // ---------- targets ----------

        public static Enemy FindEnemy(Collider col)
        {
            var er = col.GetComponentInParent<EnemyRigidbody>();
            if (er && er.enemy) return er.enemy;
            var e = col.GetComponentInParent<Enemy>();
            if (e) return e;
            var ep = col.GetComponentInParent<EnemyParent>();
            return ep ? ep.GetComponentInChildren<Enemy>() : null;
        }

        public static bool IsLocalPlayer(Collider col)
        {
            var ctrl = PlayerController.instance ? PlayerController.instance.transform : null;
            if (ctrl && col.transform.IsChildOf(ctrl)) return true;
            var pa = col.GetComponentInParent<PlayerAvatar>();
            return pa && pa == PlayerAvatar.instance;
        }

        public struct Hit
        {
            public RaycastHit Ray;
            public Enemy Enemy;
            public PlayerAvatar Player;
            public McBlock Block;
            public PhysGrabObject Pgo;
        }

        /// Sweeps a projectile one step. Skips triggers (torches, fire), stored objects and its shooter: the local
        /// player by default, or `shooter` for another player's projectile (which can then hit the local player).
        public static bool CastProjectile(Vector3 from, Vector3 step, out Hit hit, PlayerAvatar shooter = null, bool remote = false)
        {
            hit = new Hit();
            float dist = step.magnitude;
            if (dist < 1e-5f) return false;
            var hits = Physics.RaycastAll(from, step / dist, dist, ~0, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                var col = h.collider;
                bool local = IsLocalPlayer(col);
                if (local && !remote) continue;
                var player = local ? PlayerAvatar.instance : col.GetComponentInParent<PlayerAvatar>();
                if (shooter && player == shooter) continue;
                var enemy = FindEnemy(col);
                if (col.isTrigger && !enemy) continue;
                var pgo = col.GetComponentInParent<PhysGrabObject>();
                if (pgo && Storage.IsStored(pgo)) continue;
                if (col.GetComponentInParent<ArrowEntity>()) continue;
                hit.Ray = h;
                hit.Enemy = enemy;
                hit.Pgo = pgo;
                hit.Player = enemy ? null : player;
                hit.Block = col.GetComponentInParent<McBlock>();
                return true;
            }
            return false;
        }

        // ---------- damage ----------

        static readonly Dictionary<Enemy, float> enemyHurtUntil = new Dictionary<Enemy, float>();
        static float playerHurtUntil;

        /// Minecraft damage to a R.E.P.O. enemy, with Minecraft's 10-tick invulnerability. Host only.
        /// Returns false when the enemy was still invulnerable.
        public static bool HurtEnemy(Enemy e, float mcDamage, Vector3 dir, Vector3 push, float stun)
        {
            if (!e || !SemiFunc.IsMasterClientOrSingleplayer()) return false;
            if (enemyHurtUntil.TryGetValue(e, out var until) && Time.time < until) return false;
            enemyHurtUntil[e] = Time.time + 0.5f;
            int dmg = Mathf.Max(1, Mathf.RoundToInt(mcDamage * Plugin.DamageScale.Value));
            if (e.HasHealth && e.Health) e.Health.Hurt(dmg, dir);
            if (push != Vector3.zero && e.HasRigidbody && e.Rigidbody && e.Rigidbody.rb)
                e.Rigidbody.rb.AddForce(Vector3.ClampMagnitude(push, 16f), ForceMode.VelocityChange);
            if (stun > 0f && e.HasStateStunned && e.StateStunned) e.StateStunned.Set(stun);
            return true;
        }

        /// Throws an enemy (a velocity change). `yank`: its legs stop holding it in place for a moment (the fishing rod).
        /// Enemies move on the host: a client asks it.
        public static void PushEnemy(Enemy e, Vector3 vel, bool yank)
        {
            if (!e) return;
            if (!SemiFunc.IsMasterClientOrSingleplayer()) { McNet.SendPushEnemy(e, vel, yank); return; }
            if (!e.HasRigidbody || !e.Rigidbody || !e.Rigidbody.rb) return;
            if (yank) e.Rigidbody.DisableFollowPosition(0.6f, 2f);
            e.Rigidbody.rb.AddForce(vel, ForceMode.VelocityChange);
        }

        /// Knocks a player over and throws them (a velocity change). R.E.P.O. takes tumble requests from the host or the
        /// player themselves, so a client asks the host.
        public static void YankPlayer(PlayerAvatar pa, Vector3 vel)
        {
            if (!pa) return;
            if (!SemiFunc.IsMasterClientOrSingleplayer()) { McNet.SendYankPlayer(pa, vel); return; }
            if (!pa.tumble || !pa.tumble.rb) return;
            pa.tumble.TumbleRequest(true, false);
            pa.tumble.TumbleForce(vel * pa.tumble.rb.mass);
        }

        /// Throws a R.E.P.O. object (a velocity change). The throw and its landing don't count as impacts (valuables would
        /// lose value). Objects move on the host: a client asks it.
        public static void PushObject(PhysGrabObject o, Vector3 vel)
        {
            if (!o) return;
            if (!SemiFunc.IsMasterClientOrSingleplayer()) { McNet.SendPushObject(o, vel); return; }
            if (!o.rb || o.rb.isKinematic) return;
            if (o.impactDetector) o.impactDetector.ImpactDisable(1.5f);
            o.rb.WakeUp();
            o.rb.AddForce(vel, ForceMode.VelocityChange);
        }

        /// Minecraft damage to the local player (fire, pearl landing, explosion), in R.E.P.O. health.
        public static void HurtLocalPlayer(float mcDamage, bool invulnerability = true)
        {
            var pa = PlayerAvatar.instance;
            if (!pa || pa.deadSet || pa.isDisabled || !pa.playerHealth) return;
            if (invulnerability && Time.time < playerHurtUntil) return;
            playerHurtUntil = Time.time + 0.5f;
            pa.playerHealth.Hurt(Mathf.Max(1, Mathf.RoundToInt(mcDamage * Plugin.DamageScale.Value)), false);
            McAssets.Play("hurt", pa.transform.position, 0.6f, McAssets.RandPitch());
        }

        public static Vector3 EnemyCenter(Enemy e)
        {
            if (e.HasRigidbody && e.Rigidbody) return e.Rigidbody.transform.position;
            return e.CenterTransform ? e.CenterTransform.position : e.transform.position;
        }

        // ---------- placement ----------

        public static bool CanPlaceBlock(Vector3 center, Quaternion rot)
            => !Physics.CheckBox(center, Vector3.one * B * 0.47f, rot, PlaceMask, QueryTriggerInteraction.Ignore);

        /// Flint and steel: fire goes in the space in front of the clicked face and needs a floor under it.
        public static FireBlock PlaceFire(Vector3 point, Vector3 normal)
        {
            var p = point + normal * B * 0.3f + Vector3.up * 0.05f;
            if (!Physics.Raycast(p, Vector3.down, out var h, B * 1.5f, GroundMask, QueryTriggerInteraction.Ignore)) return null;
            if (h.normal.y < 0.5f) return null;
            return McNet.PlaceFire(h.point);
        }

        // ---------- teleport (ender pearl) ----------

        public static void TeleportLocalPlayer(Vector3 point, Vector3 normal)
        {
            var pc = PlayerController.instance;
            var pa = PlayerAvatar.instance;
            if (!pc || !pa || pa.deadSet || pa.isDisabled || pa.isTumbling) return;

            Vector3 feet;
            if (normal.y > 0.5f) feet = point + Vector3.up * 0.05f;
            else if (normal.y < -0.5f) feet = point - Vector3.up * 1.7f;
            else
            {
                var p = point + normal * 0.45f;
                feet = Physics.Raycast(p + Vector3.up * 0.2f, Vector3.down, out var h, 2.5f, GroundMask, QueryTriggerInteraction.Ignore)
                    ? h.point + Vector3.up * 0.05f : p - Vector3.up * 0.9f;
            }

            var from = pc.transform.position;
            Particles.Portal(from + Vector3.up, 32);
            McAssets.Play("teleport", from, 1f, 1f);

            pc.transform.position = feet;
            if (pc.rb) { pc.rb.position = feet; pc.rb.velocity = Vector3.zero; }
            pa.transform.position = feet;
            if (pc.CollisionController) pc.CollisionController.fallDistance = 0f;

            Particles.Portal(feet + Vector3.up, 32);
            McAssets.Play("teleport", feet, 1f, 1f);
            HurtLocalPlayer(5f, false); // Minecraft: 5 fall damage per pearl
        }

        // ---------- lights ----------

        static readonly List<Light> worldLights = new List<Light>();
        static bool checkedRendering;
        float cullTimer;

        /// A torch/fire light. Only the MaxLights nearest the camera are switched on.
        public static Light AddLight(Transform parent, Vector3 localPos, Color color, float range, float intensity)
        {
            CheckRendering();
            var go = new GameObject("mc_light");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = color;
            l.range = range;
            l.intensity = intensity;
            l.shadows = LightShadows.None;
            l.renderMode = LightRenderMode.ForcePixel;
            worldLights.Add(l);
            return l;
        }

        static void CheckRendering()
        {
            if (checkedRendering) return;
            checkedRendering = true;
            var cam = Camera.main;
            var path = cam ? cam.actualRenderingPath : RenderingPath.UsePlayerSettings;
            Plugin.Log.LogInfo("Rendering path " + path + ", pixel lights " + QualitySettings.pixelLightCount);
            // forward rendering drops lights past pixelLightCount to per-vertex; torches need real per-pixel lights
            if (path == RenderingPath.Forward && QualitySettings.pixelLightCount < 8) QualitySettings.pixelLightCount = 8;
        }

        void CullLights()
        {
            cullTimer -= Time.deltaTime;
            if (cullTimer > 0f) return;
            cullTimer = 0.25f;
            worldLights.RemoveAll(l => l == null);
            var cam = Camera.main;
            if (!cam) return;
            var c = cam.transform.position;
            worldLights.Sort((a, b) => (a.transform.position - c).sqrMagnitude.CompareTo((b.transform.position - c).sqrMagnitude));
            int max = Mathf.Max(1, Plugin.MaxLights.Value);
            for (int i = 0; i < worldLights.Count; i++) worldLights[i].enabled = i < max;
        }

        /// A short bright flash for explosions.
        public static void Flash(Vector3 pos, float range)
        {
            var go = new GameObject("mc_flash");
            go.transform.position = pos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.85f, 0.6f);
            l.range = range;
            l.intensity = 6f;
            l.renderMode = LightRenderMode.ForcePixel;
            go.AddComponent<FadeLight>();
        }

        class FadeLight : MonoBehaviour
        {
            float t;
            Light l;
            void Awake() => l = GetComponent<Light>();
            void Update()
            {
                t += Time.deltaTime;
                l.intensity = Mathf.Lerp(6f, 0f, t / 0.35f);
                if (t >= 0.35f) Destroy(gameObject);
            }
        }

        // held torch (dynamic light, so a torch in the off hand works like a lantern) and the flashlight item
        Light heldTorch, flashlight;

        void UpdateHeldLights()
        {
            var p = McPlayer.Instance;
            var cam = Camera.main;
            bool active = p && p.Active && cam;
            bool torch = active && (IsHeld(p, "torch"));
            bool flash = active && p.FlashlightOn && IsHeld(p, "flashlight");

            if (torch)
            {
                if (!heldTorch) heldTorch = MakeLight("mc_held_torch", LightType.Point, new Color(1f, 0.78f, 0.5f), 10f * B, 1.1f);
                int side = HeldSide(p, "torch");
                heldTorch.transform.position = cam.transform.position + cam.transform.right * 0.3f * side - cam.transform.up * 0.15f + cam.transform.forward * 0.3f;
            }
            if (heldTorch) heldTorch.enabled = torch;

            if (flash)
            {
                if (!flashlight) flashlight = MakeFlashlight();
                int side = HeldSide(p, "flashlight");
                flashlight.transform.SetPositionAndRotation(
                    cam.transform.position + cam.transform.right * 0.2f * side - cam.transform.up * 0.15f, cam.transform.rotation);
            }
            if (flashlight) flashlight.enabled = flash;
        }

        static bool IsHeld(McPlayer p, string id) => (p.Inventory.Held != null && p.Inventory.Held.IsMc(id)) || (p.Inventory.Offhand != null && p.Inventory.Offhand.IsMc(id));
        static int HeldSide(McPlayer p, string id) => p.Inventory.Held != null && p.Inventory.Held.IsMc(id) ? 1 : -1;

        Light MakeLight(string name, LightType type, Color color, float range, float intensity)
        {
            var go = new GameObject(name);
            DontDestroyOnLoad(go);
            var l = go.AddComponent<Light>();
            l.type = type;
            l.color = color;
            l.range = range;
            l.intensity = intensity;
            l.shadows = LightShadows.None;
            l.renderMode = LightRenderMode.ForcePixel;
            return l;
        }

        /// The flashlight item reuses the look of R.E.P.O.'s own flashlight beam.
        Light MakeFlashlight()
        {
            var l = MakeLight("mc_flashlight", LightType.Spot, new Color(1f, 0.95f, 0.85f), 20f, 1.5f);
            var src = FlashlightController.Instance ? FlashlightController.Instance.spotlight : null;
            if (src)
            {
                l.color = src.color;
                l.range = src.range;
                l.spotAngle = src.spotAngle;
                l.innerSpotAngle = src.innerSpotAngle;
                l.intensity = Mathf.Max(1f, src.intensity);
                l.cookie = src.cookie;
                l.shadows = src.shadows;
                l.shadowStrength = src.shadowStrength;
                l.cullingMask = src.cullingMask;
            }
            else l.spotAngle = 60f;
            return l;
        }
    }

    // =====================================================================================================
    // Blocks
    // =====================================================================================================

    /// A Minecraft block placed in the R.E.P.O. level: TNT, torch or fire.
    internal abstract class McBlock : MonoBehaviour
    {
        public static readonly List<McBlock> All = new List<McBlock>();

        /// Item dropped when broken (null = nothing).
        public abstract string ItemId { get; }
        public abstract Vector3 Center { get; }

        /// Multiplayer id (0 = exists only on this PC, like fire) and the actor number of the player who placed it.
        public int NetId, Owner;

        McBlock support;
        bool hasSupport;

        /// The block this one stands on (a torch on TNT pops off when the TNT goes).
        public McBlock Support { get => support; set { support = value; hasSupport = value != null; } }

        protected virtual void OnEnable() => All.Add(this);
        protected virtual void OnDisable() => All.Remove(this);
        protected virtual void OnDestroy() => McNet.Forget(this);

        protected virtual void Update()
        {
            // every client sees the support go; only the deciding one drops the (shared) item
            if (hasSupport && support == null) Break(McNet.Resolves(Owner));
        }

        /// Removes it here only. `drop` drops the item for every player, so only one client may pass true.
        public virtual void Break(bool drop)
        {
            if (drop && ItemId != null)
                ItemEntity.Drop(ItemStack.Mc(ItemId), Center, new Vector3(Random.Range(-1f, 1f), 2.5f, Random.Range(-1f, 1f)), 0.5f);
            Destroy(gameObject);
        }

        /// Removes it for every player. `effects`: break sound and particles; `drop`: this client drops the item.
        public void BreakSynced(bool effects, bool drop)
        {
            McNet.SendBreak(this, effects);
            if (effects) BreakEffects();
            Break(drop);
        }

        public virtual void BreakEffects() { }

        protected static void AddTrigger(GameObject go, Vector3 center, Vector3 size)
        {
            var hit = new GameObject("mc_hitbox");
            hit.transform.SetParent(go.transform, false);
            hit.layer = 2; // Ignore Raycast: R.E.P.O.'s own rays skip it, the Minecraft crosshair still finds it
            var c = hit.AddComponent<BoxCollider>();
            c.isTrigger = true;
            c.center = center;
            c.size = size;
        }
    }

    internal class TntBlock : McBlock
    {
        public override string ItemId => "tnt";
        public override Vector3 Center => transform.position;

        public static TntBlock Place(Vector3 center, Quaternion rot)
        {
            var go = new GameObject("mc_tnt");
            go.transform.SetPositionAndRotation(center, rot);
            McItems.TntModel(McWorld.B).transform.SetParent(go.transform, false);
            go.AddComponent<BoxCollider>().size = Vector3.one * McWorld.B;
            return go.AddComponent<TntBlock>();
        }

        public override void BreakEffects()
        {
            McAssets.Play("grass", Center, 1f, 0.8f);
            Particles.Burst(Center, Vector3.one * McWorld.B * 0.3f, McAssets.TntSide, 12, 1.5f);
        }

        /// Lit by flint and steel (4 s fuse), fire, a burning arrow or another explosion (short fuse).
        /// Lights it for every player; whoever lit it decides the explosion when the host doesn't have the mod.
        bool primed;

        public void Prime(float fuse)
        {
            if (!this || primed) return;
            float angle = Random.value * Mathf.PI * 2f;
            McNet.SendPrime(NetId, fuse, angle, transform.position, transform.rotation, McNet.LocalActor);
            PrimeLocal(fuse, angle, McNet.LocalActor);
        }

        public void PrimeLocal(float fuse, float angle, int primer)
        {
            if (!this || primed) return;
            primed = true;
            GetComponent<Collider>().enabled = false;
            PrimedTnt.Spawn(NetId, transform.position, transform.rotation, fuse, angle, primer);
            Destroy(gameObject);
        }
    }

    /// Lit TNT: falls, flashes white, swells, then explodes with power 4. In multiplayer only one client decides the
    /// explosion (McNet.Resolves); the others hold it at full swell until its Explode arrives.
    internal class PrimedTnt : MonoBehaviour
    {
        public int NetId;
        int primer;
        float fuse, waited;
        Rigidbody rb;
        MeshRenderer mr;
        Transform cube;

        /// angle: direction of Minecraft's little hop, the same on every client.
        public static void Spawn(int netId, Vector3 pos, Quaternion rot, float fuse, float angle, int primer)
        {
            float B = McWorld.B;
            var go = new GameObject("mc_primed_tnt");
            go.transform.SetPositionAndRotation(pos, rot);
            var model = McItems.TntModel(B);
            model.transform.SetParent(go.transform, false);
            go.AddComponent<BoxCollider>().size = Vector3.one * B * 0.98f;
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 2f;
            rb.freezeRotation = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            // Minecraft: a little hop in a random direction
            rb.velocity = new Vector3(-Mathf.Sin(angle) * 0.02f, 0.2f, -Mathf.Cos(angle) * 0.02f) * 20f * B;
            var p = go.AddComponent<PrimedTnt>();
            p.NetId = netId;
            p.primer = primer;
            p.fuse = fuse;
            p.rb = rb;
            p.mr = model.GetComponentInChildren<MeshRenderer>();
            p.cube = p.mr.transform;
            McNet.Register(p);
            McAssets.Play("fuse", pos, 1f, 1f);
        }

        void OnDestroy() => McNet.Forget(this);

        /// Taken away by the deciding client's explosion.
        public void Remove()
        {
            GetComponent<Collider>().enabled = false;
            Destroy(gameObject);
        }

        void Update()
        {
            fuse -= Time.deltaTime;
            float B = McWorld.B;
            int ticks = Mathf.Max(0, (int)(fuse * 20f));
            mr.sharedMaterial = (ticks / 5) % 2 == 0 ? McItems.TntFlashMat : McItems.TntMat;
            if (fuse < 0.5f)
            {
                float f = 1f - Mathf.Clamp01(fuse / 0.5f);
                f *= f; f *= f;
                cube.localScale = Vector3.one * B * (1f + f * 0.3f);
            }
            if (Random.value < Time.deltaTime * 10f)
                Particles.SmokePuff(transform.position + Vector3.up * B * 0.6f, B * 0.2f, 1, new Color(0.6f, 0.6f, 0.6f, 0.8f));
            if (fuse > 0f) return;
            // not ours to decide: wait for the explosion, unless that player is gone (then decide it here)
            if (!McNet.Resolves(primer) && (waited += Time.deltaTime) < 1.5f) return;
            var pos = transform.position;
            McNet.MarkExploded(NetId);
            McNet.SendExplode(NetId, pos);
            Remove();
            Explosion.Explode(pos, 4f);
        }
    }

    internal class TorchBlock : McBlock
    {
        public override string ItemId => "torch";
        public override Vector3 Center => flame ? flame.position : transform.position;

        Transform flame;
        float particleTimer;

        /// Standing on a floor, or leaning 22.5 degrees off a wall like Minecraft's wall torch. Not on ceilings.
        public static TorchBlock Place(Vector3 point, Vector3 normal, McBlock support)
        {
            float B = McWorld.B;
            bool wall = normal.y < 0.7f;
            var rot = Quaternion.identity;
            var pos = point;
            if (wall)
            {
                var n = Vector3.ProjectOnPlane(normal, Vector3.up).normalized;
                var up = (Vector3.up * Mathf.Cos(22.5f * Mathf.Deg2Rad) + n * Mathf.Sin(22.5f * Mathf.Deg2Rad)).normalized;
                rot = Quaternion.LookRotation(Vector3.ProjectOnPlane(-n, up), up);
                pos = point + n * (B / 16f) - Vector3.up * (B * 3f / 16f);
            }
            var go = new GameObject("mc_torch");
            go.transform.SetPositionAndRotation(pos, rot);

            var model = new GameObject("model");
            model.transform.SetParent(go.transform, false);
            model.transform.localScale = Vector3.one * B;
            model.AddComponent<MeshFilter>().sharedMesh = McItems.TorchMesh;
            var mr = model.AddComponent<MeshRenderer>();
            mr.sharedMaterial = McItems.TorchMat;
            mr.shadowCastingMode = ShadowCastingMode.Off;

            AddTrigger(go, new Vector3(0, B * 0.33f, 0), new Vector3(B * 0.3f, B * 0.7f, B * 0.3f));

            var t = go.AddComponent<TorchBlock>();
            t.flame = new GameObject("flame").transform;
            t.flame.SetParent(go.transform, false);
            t.flame.localPosition = new Vector3(0, B * 0.7f, 0);
            // light level 14: lights roughly 13 blocks
            McWorld.AddLight(t.flame, Vector3.zero, new Color(1f, 0.78f, 0.5f), 13f * B, 1.2f);
            t.Support = support;
            return t;
        }

        public override void BreakEffects()
        {
            McAssets.Play("wood", Center, 1f, 0.8f);
            Particles.Burst(Center, Vector3.one * McWorld.B * 0.3f, McAssets.Torch, 12, 1.5f);
        }

        protected override void Update()
        {
            base.Update();
            particleTimer -= Time.deltaTime;
            if (particleTimer > 0f || !flame) return;
            particleTimer = Random.Range(0.3f, 0.8f);
            Particles.Flame(flame.position, McWorld.B * 0.09f);
            if (Random.value < 0.4f) Particles.SmokePuff(flame.position + Vector3.up * McWorld.B * 0.1f, McWorld.B * 0.1f, 1, new Color(0.2f, 0.2f, 0.2f, 0.8f));
        }
    }

    /// Fire: animated block-fire planes that burn out after a while, light the area, set anything standing in
    /// them on fire and ignite nearby TNT. Punch it to put it out.
    internal class FireBlock : McBlock
    {
        public static readonly List<FireBlock> Fires = new List<FireBlock>();
        public override string ItemId => null;
        public override Vector3 Center => transform.position + Vector3.up * McWorld.B * 0.5f;

        float life, damageTimer, soundTimer;
        int frameOffset;
        Light glow;
        MeshRenderer sides, cross;
        MaterialPropertyBlock mpb;
        float seed;

        static Mesh sidesMesh, crossMesh;

        /// Minecraft's floor fire: four planes around the block and two crossed through it, 1.4 blocks tall.
        public static Mesh SidesMesh
        {
            get
            {
                if (sidesMesh) return sidesMesh;
                var mb = new MeshBuilder();
                const float e = 0.47f, h = 1.4f;
                mb.DoubleQuad(new Vector3(-e, 0, e), new Vector3(e, 0, e), new Vector3(e, h, e), new Vector3(-e, h, e));
                mb.DoubleQuad(new Vector3(e, 0, -e), new Vector3(-e, 0, -e), new Vector3(-e, h, -e), new Vector3(e, h, -e));
                mb.DoubleQuad(new Vector3(e, 0, e), new Vector3(e, 0, -e), new Vector3(e, h, -e), new Vector3(e, h, e));
                mb.DoubleQuad(new Vector3(-e, 0, -e), new Vector3(-e, 0, e), new Vector3(-e, h, e), new Vector3(-e, h, -e));
                return sidesMesh = mb.Build("mc_fire_sides");
            }
        }

        public static Mesh CrossMesh
        {
            get
            {
                if (crossMesh) return crossMesh;
                var mb = new MeshBuilder();
                const float e = 0.5f, h = 1.4f;
                mb.DoubleQuad(new Vector3(-e, 0, -e), new Vector3(e, 0, e), new Vector3(e, h, e), new Vector3(-e, h, -e));
                mb.DoubleQuad(new Vector3(-e, 0, e), new Vector3(e, 0, -e), new Vector3(e, h, -e), new Vector3(-e, h, e));
                return crossMesh = mb.Build("mc_fire_cross");
            }
        }

        /// Seconds left before it burns out (shared so it goes out at the same time everywhere).
        public float Life => life;

        /// life: -1 = random 6-12 s.
        public static FireBlock Place(Vector3 floor, float life = -1f)
        {
            float B = McWorld.B;
            foreach (var f in Fires)
                if (f && (f.transform.position - floor).sqrMagnitude < B * B * 0.36f) return null;
            var go = new GameObject("mc_fire");
            go.transform.position = floor;
            var fire = go.AddComponent<FireBlock>();
            fire.Owner = McNet.LocalActor;
            fire.sides = Part(go, "sides", SidesMesh, McAssets.Fire0[0]);
            fire.cross = Part(go, "cross", CrossMesh, McAssets.Fire1[0]);
            AddTrigger(go, new Vector3(0, B * 0.5f, 0), Vector3.one * B);
            fire.glow = McWorld.AddLight(go.transform, Vector3.up * B * 0.7f, new Color(1f, 0.62f, 0.3f), 15f * B, 1.6f);
            fire.life = life > 0f ? life : Random.Range(6f, 12f);
            fire.frameOffset = Random.Range(0, 32);
            fire.seed = Random.value * 100f;
            fire.soundTimer = Random.Range(0.2f, 1f);
            return fire;
        }

        static MeshRenderer Part(GameObject parent, string name, Mesh mesh, Texture2D tex)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localScale = Vector3.one * McWorld.B;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = Mats.SpriteTex(tex);
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mr;
        }

        protected override void OnEnable() { base.OnEnable(); Fires.Add(this); }
        protected override void OnDisable() { base.OnDisable(); Fires.Remove(this); }

        public static Texture2D Frame(Texture2D[] frames, float time, int offset)
            => frames[((int)(time * 20f) + offset) % frames.Length];

        protected override void Update()
        {
            base.Update();
            float dt = Time.deltaTime;
            float B = McWorld.B;

            if (mpb == null) mpb = new MaterialPropertyBlock();
            mpb.SetTexture("_MainTex", Frame(McAssets.Fire0, Time.time, frameOffset));
            sides.SetPropertyBlock(mpb);
            mpb.SetTexture("_MainTex", Frame(McAssets.Fire1, Time.time, frameOffset + 7));
            cross.SetPropertyBlock(mpb);
            if (glow) glow.intensity = 1.3f + Mathf.PerlinNoise(Time.time * 6f, seed) * 0.7f;

            soundTimer -= dt;
            if (soundTimer <= 0f)
            {
                soundTimer = Random.Range(1f, 2.5f);
                McAssets.Play("fire", Center, 0.5f + Random.value * 0.5f, Random.value * 0.7f + 0.3f);
            }
            if (Random.value < dt * 2f) Particles.SmokePuff(Center + Vector3.up * B * 0.6f, B * 0.35f, 1, new Color(0.15f, 0.15f, 0.15f, 0.7f));

            damageTimer -= dt;
            if (damageTimer <= 0f)
            {
                damageTimer = 0.25f;
                Contacts(B);
            }

            life -= dt;
            if (life <= 0f) Destroy(gameObject); // burns out on R.E.P.O.'s non-flammable floors
        }

        void Contacts(float B)
        {
            var c = Center;
            if (SemiFunc.IsMasterClientOrSingleplayer())
            {
                var burned = new HashSet<Enemy>();
                foreach (var col in Physics.OverlapBox(c, new Vector3(B * 0.5f, B * 0.7f, B * 0.5f), Quaternion.identity, ~0, QueryTriggerInteraction.Collide))
                {
                    var e = McWorld.FindEnemy(col);
                    if (!e || !burned.Add(e)) continue;
                    Burning.IgniteEnemy(e, 8f);
                    McWorld.HurtEnemy(e, 1f, Vector3.up, Vector3.zero, 0f); // Minecraft "in fire" damage
                }
            }
            var p = McPlayer.Instance;
            if (p && p.Active)
            {
                var d = p.FeetPosition - transform.position;
                if (new Vector2(d.x, d.z).magnitude < B * 0.5f + 0.3f && d.y > -0.3f && d.y < B * 1.2f)
                {
                    Burning.IgnitePlayer(8f);
                    McWorld.HurtLocalPlayer(1f);
                }
            }
            // TNT is flammable: fire next to it lights it within a second or so (decided by one client, so more
            // players don't light it faster)
            if (!McNet.Resolves(Owner)) return;
            foreach (var b in McBlock.All.ToArray())
                if (b is TntBlock tnt && (tnt.Center - c).sqrMagnitude < B * B * 1.7f && Random.value < 0.25f) tnt.Prime(4f);
        }

        public override void Break(bool drop)
        {
            McAssets.Play("fizz", Center, 0.5f, 2.6f + (Random.value - Random.value) * 0.8f);
            Particles.SmokePuff(Center, McWorld.B * 0.4f, 6, new Color(0.4f, 0.4f, 0.4f, 0.8f));
            Destroy(gameObject);
        }
    }

    // =====================================================================================================
    // Explosions and burning
    // =====================================================================================================

    internal static class Explosion
    {
        /// Minecraft's explosion: damage = (i^2 + i) / 2 * 7 * 2P + 1 with i = (1 - d / 2P) * exposure, knockback i.
        /// Damages and throws enemies and players, pushes physics objects and items, chains into nearby TNT.
        /// `resolve`: this client decides the explosion (lights TNT and knocks out torches for everyone); the others
        /// only show it and put out their own fire. World damage stays with the host either way.
        public static void Explode(Vector3 pos, float power, bool resolve = true)
        {
            float B = McWorld.B;
            float r = 2f * power * B;

            Particles.Explosion(pos, power * B);
            McAssets.Play("explode", pos, 1f, (1f + (Random.value - Random.value) * 0.2f) * 0.7f, 80f);
            McWorld.Flash(pos, r * 2f);
            if (GameDirector.instance)
            {
                if (GameDirector.instance.CameraShake) GameDirector.instance.CameraShake.ShakeDistance(8f, r, r * 3f, pos, 0.6f);
                if (GameDirector.instance.CameraImpact) GameDirector.instance.CameraImpact.ShakeDistance(12f, r, r * 3f, pos, 0.25f);
            }

            // blocks inside the blast: TNT gets lit with a short fuse, torches and fire are knocked out
            foreach (var b in McBlock.All.ToArray())
            {
                if (!b || (b.Center - pos).magnitude > power * B * 1.3f) continue;
                if (b.NetId == 0) b.Break(false); // exists only on this PC
                else if (!resolve) continue;      // the deciding client sends these
                else if (b is TntBlock tnt) tnt.Prime(Random.Range(0.5f, 1.5f));
                else b.BreakSynced(false, b is TorchBlock); // fire just goes out
            }

            foreach (var it in ItemEntity.All)
            {
                var d = it.transform.position - pos;
                float i = Impact(d.magnitude, r, pos, it.transform.position + Vector3.up * 0.1f, 0f);
                if (i > 0f) it.Push(Dir(d) * i * 20f * B + Vector3.up * i * 4f);
            }

            if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
            if (EnemyDirector.instance) EnemyDirector.instance.SetInvestigate(pos, r * 3f);

            // enemies
            var seen = new HashSet<Enemy>();
            foreach (var e in Object.FindObjectsOfType<Enemy>())
            {
                if (!e || !seen.Add(e)) continue;
                var c = McWorld.EnemyCenter(e);
                float i = Impact((c - pos).magnitude, r, pos, c, 0.5f);
                if (i <= 0f) continue;
                var dir = Dir(c - pos);
                McWorld.HurtEnemy(e, Damage(i, power), dir, dir * i * 20f * B + Vector3.up * i * 6f, i * 3f);
            }

            // players
            foreach (var pa in SemiFunc.PlayerGetAll())
            {
                if (!pa || pa.isDisabled || pa.deadSet || !pa.playerHealth) continue;
                var c = pa.transform.position + Vector3.up * 0.9f;
                float i = Impact((c - pos).magnitude, r, pos, c, 0.6f);
                if (i <= 0f) continue;
                var dir = Dir(c - pos);
                if (pa.isLocal && McPlayer.Instance && McPlayer.Instance.ShieldBlocks(pos))
                {
                    McPlayer.Instance.OnShieldBlock(pos, null, Damage(i, power));
                    continue;
                }
                int hp = Mathf.Max(1, Mathf.RoundToInt(Damage(i, power) * Plugin.DamageScale.Value));
                if (pa.isLocal) pa.playerHealth.Hurt(hp, false);
                else pa.playerHealth.HurtOther(hp, Vector3.zero, false);
                if (i > 0.15f && pa.tumble)
                {
                    pa.tumble.TumbleRequest(true, false);
                    pa.tumble.TumbleForce((dir + Vector3.up * 0.5f).normalized * i * 45f);
                }
            }

            // physics objects (valuables, carts, doors, primed TNT)
            var bodies = new HashSet<Rigidbody>();
            int mask = LayerMask.GetMask("Default", "PhysGrabObject", "PhysGrabObjectCart", "PhysGrabObjectHinge");
            foreach (var col in Physics.OverlapSphere(pos, r, mask, QueryTriggerInteraction.Ignore))
            {
                var rb = col.attachedRigidbody;
                if (!rb || rb.isKinematic || !bodies.Add(rb)) continue;
                if (rb.GetComponentInParent<PlayerAvatar>() || rb.GetComponentInParent<EnemyRigidbody>()) continue;
                if (PlayerController.instance && rb == PlayerController.instance.rb) continue;
                var pgo = rb.GetComponent<PhysGrabObject>();
                if (pgo && Storage.IsStored(pgo)) continue;
                var c = rb.worldCenterOfMass;
                float i = Impact((c - pos).magnitude, r, pos, c, 0f);
                if (i <= 0f) continue;
                rb.WakeUp();
                rb.AddForce(Dir(c - pos) * i * 20f * B + Vector3.up * i * 3f, ForceMode.VelocityChange);
                rb.AddTorque(Random.insideUnitSphere * i * 8f, ForceMode.VelocityChange);
            }
        }

        static Vector3 Dir(Vector3 d) => d.sqrMagnitude < 1e-4f ? Vector3.up : d.normalized;

        static float Damage(float i, float power) => (i * i + i) / 2f * 7f * power * 2f + 1f;

        static float Impact(float dist, float radius, Vector3 pos, Vector3 target, float halfHeight)
        {
            float d = dist / radius;
            if (d > 1f) return 0f;
            return (1f - d) * Exposure(pos, target, halfHeight);
        }

        /// Fraction of sample points on the target that the blast can see (Minecraft's seen-percent).
        static float Exposure(Vector3 pos, Vector3 target, float halfHeight)
        {
            int clear = 0, total = 0;
            float w = halfHeight * 0.5f;
            var offsets = new[] { Vector3.zero, Vector3.up * halfHeight, Vector3.down * halfHeight, Vector3.right * w, Vector3.left * w, Vector3.forward * w, Vector3.back * w };
            foreach (var o in offsets)
            {
                total++;
                if (!Physics.Linecast(pos, target + o, McWorld.StaticMask, QueryTriggerInteraction.Ignore)) clear++;
                if (halfHeight <= 0f) break;
            }
            return clear / (float)total;
        }
    }

    /// Minecraft's on-fire status: 1 damage a second, flames drawn around the burning thing.
    internal static class Burning
    {
        class Entry { public float Until; public float Height = 1.5f, Width = 0.8f; }
        static readonly Dictionary<Enemy, Entry> enemies = new Dictionary<Enemy, Entry>();
        static float playerUntil, tickTimer;
        static MaterialPropertyBlock mpb;

        public static bool PlayerOnFire => Time.time < playerUntil && McPlayer.Instance && McPlayer.Instance.Active;
        public static bool IsBurning(Enemy e) => e && enemies.TryGetValue(e, out var x) && Time.time < x.Until;

        public static void IgnitePlayer(float seconds) => playerUntil = Mathf.Max(playerUntil, Time.time + seconds);

        public static void IgniteEnemy(Enemy e, float seconds)
        {
            if (!e) return;
            if (!enemies.TryGetValue(e, out var x))
            {
                x = new Entry();
                var root = e.HasRigidbody && e.Rigidbody ? e.Rigidbody.transform : e.transform;
                bool any = false;
                var b = new Bounds();
                foreach (var c in root.GetComponentsInChildren<Collider>())
                {
                    if (c.isTrigger) continue;
                    if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
                }
                if (any) { x.Height = Mathf.Clamp(b.size.y, 0.5f, 4f); x.Width = Mathf.Clamp(Mathf.Max(b.size.x, b.size.z), 0.4f, 3f); }
                enemies[e] = x;
            }
            x.Until = Mathf.Max(x.Until, Time.time + seconds);
        }

        public static void Tick(float dt)
        {
            tickTimer -= dt;
            if (tickTimer > 0f) return;
            tickTimer = 1f;
            var dead = new List<Enemy>();
            foreach (var kv in enemies)
            {
                if (!kv.Key || Time.time >= kv.Value.Until) { dead.Add(kv.Key); continue; }
                McWorld.HurtEnemy(kv.Key, 1f, Vector3.up, Vector3.zero, 0f);
            }
            foreach (var d in dead) enemies.Remove(d);
            if (PlayerOnFire) McWorld.HurtLocalPlayer(1f);
        }

        /// Fire planes around burning enemies (and the local player in third person).
        public static void Draw()
        {
            if (enemies.Count == 0 && !PlayerOnFire) return;
            if (mpb == null) mpb = new MaterialPropertyBlock();
            var mat = Mats.SpriteTex(McAssets.Fire0[0]);
            mpb.SetTexture("_MainTex", FireBlock.Frame(McAssets.Fire1, Time.time, 0));
            foreach (var kv in enemies)
            {
                if (!kv.Key || Time.time >= kv.Value.Until) continue;
                var c = McWorld.EnemyCenter(kv.Key);
                var basePos = c - Vector3.up * kv.Value.Height * 0.5f;
                var scale = new Vector3(kv.Value.Width, kv.Value.Height / 1.4f, kv.Value.Width);
                Graphics.DrawMesh(FireBlock.CrossMesh, Matrix4x4.TRS(basePos, Quaternion.identity, scale), mat, 0, null, 0, mpb, false, false);
                Graphics.DrawMesh(FireBlock.SidesMesh, Matrix4x4.TRS(basePos, Quaternion.Euler(0, 45, 0), scale * 0.9f), mat, 0, null, 0, mpb, false, false);
                if (Random.value < Time.deltaTime * 6f) Particles.Flame(c + Random.insideUnitSphere * kv.Value.Width * 0.4f, McWorld.B * 0.1f);
            }
            if (PlayerOnFire && SteveManager.ViewMode != 0 && PlayerAvatar.instance)
            {
                var pos = PlayerAvatar.instance.transform.position;
                Graphics.DrawMesh(FireBlock.CrossMesh, Matrix4x4.TRS(pos, Quaternion.identity, new Vector3(0.8f, 1.2f, 0.8f)), mat, 0, null, 0, mpb, false, false);
            }
        }
    }

    // =====================================================================================================
    // Projectiles
    // =====================================================================================================

    /// A Minecraft arrow: drops with gravity, damages by speed, sticks in what it hits for a minute, can be
    /// picked back up, and catches fire flying through fire (then sets enemies alight and ignites TNT).
    /// Every player flies their own copy of each arrow; the host's copy deals the damage. A stuck arrow goes to the
    /// nearest player standing on it, decided like a dropped item (ItemEntity), so only one player gets it.
    internal class ArrowEntity : MonoBehaviour
    {
        public int NetId, Owner;
        Vector3 vel;
        bool stuck, crit, onFire, pickup, hadStuckTo, remote;
        float age, claimTimer;
        readonly Dictionary<int, float> claims = new Dictionary<int, float>();
        PlayerAvatar shooter;
        Transform stuckTo;
        Vector3 stuckPos;
        Quaternion stuckRot;
        const float BaseDamage = 2f;

        static Quaternion SpriteToForward => Quaternion.Inverse(Quaternion.LookRotation(new Vector3(1f, 1f, 0f).normalized, new Vector3(0f, 0f, -1f)));

        /// remote: another player's arrow, shot by `remoteShooter` (null when they already left).
        public static ArrowEntity Shoot(Vector3 pos, Vector3 vel, bool crit, bool pickup, bool remote, PlayerAvatar remoteShooter)
        {
            var go = new GameObject("mc_arrow");
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(vel));
            var m = ModelUtil.SpriteModel(McAssets.Arrow, McWorld.B * 0.9f);
            m.transform.SetParent(go.transform, false);
            m.transform.localRotation = SpriteToForward;
            ModelUtil.SetShadows(m, ShadowCastingMode.On);
            var a = go.AddComponent<ArrowEntity>();
            a.vel = vel;
            a.crit = crit;
            a.pickup = pickup;
            a.shooter = remoteShooter;
            a.remote = remote;
            return a;
        }

        /// Destroyed this frame (Destroy is deferred, and a received arrow is stepped several times at once).
        public bool Gone { get; private set; }

        void Kill()
        {
            Gone = true;
            Destroy(gameObject);
        }

        void OnDestroy() => McNet.Forget(this);

        void Update() => Step(Time.deltaTime);

        public void Step(float dt)
        {
            if (Gone) return;
            float B = McWorld.B;
            age += dt;
            if (stuck) { StuckUpdate(); return; }

            vel.y -= 20f * B * dt;                 // 0.05 blocks/tick^2
            vel *= Mathf.Pow(0.99f, dt * 20f);
            var pos = transform.position;
            var step = vel * dt;
            if (McWorld.CastProjectile(pos, step, out var hit, shooter, remote)) { OnHit(hit); return; }
            transform.SetPositionAndRotation(pos + step, Quaternion.LookRotation(vel));

            if (!onFire)
                foreach (var f in FireBlock.Fires)
                    if (f && (f.Center - pos).sqrMagnitude < B * B * 0.5f) { onFire = true; break; }
            if (crit && Random.value < dt * 40f) Particles.Crit(pos, 1);
            if (onFire && Random.value < dt * 30f) Particles.Flame(pos, B * 0.12f);
            if (age > 30f || pos.y < -300f) Kill();
        }

        void OnHit(McWorld.Hit hit)
        {
            float B = McWorld.B;
            var point = hit.Ray.point;
            float speed = vel.magnitude / (20f * B); // blocks per tick
            int dmg = Mathf.CeilToInt(Mathf.Clamp(speed * BaseDamage, 0f, 100f));
            if (crit) dmg += Random.Range(0, dmg / 2 + 2);
            var dir = vel.normalized;

            if (hit.Enemy)
            {
                // enemies live on the host: its copy of the arrow deals the damage, the others just end here
                bool host = SemiFunc.IsMasterClientOrSingleplayer();
                var flat = Vector3.ProjectOnPlane(dir, Vector3.up).normalized;
                if (!host || McWorld.HurtEnemy(hit.Enemy, dmg, dir, flat * 2.5f + Vector3.up * 1.5f, 0f))
                {
                    if (onFire && host) Burning.IgniteEnemy(hit.Enemy, 5f);
                    McAssets.Play("bowhit", point, 1f, 1.2f / (Random.value * 0.2f + 0.9f));
                    Kill();
                    return;
                }
                // still invulnerable: Minecraft bounces the arrow off
                vel = -vel * 0.1f;
                return;
            }
            if (hit.Player)
            {
                // a player is hurt by their own copy (so their shield counts); the host stands in for players without the mod
                int hp = Mathf.Max(1, Mathf.RoundToInt(dmg * Plugin.DamageScale.Value));
                var me = McPlayer.Instance;
                if (hit.Player.isLocal)
                {
                    if (me && me.ShieldBlocks(transform.position)) me.OnShieldBlock(transform.position, null, dmg);
                    else if (hit.Player.playerHealth) hit.Player.playerHealth.Hurt(hp, false);
                }
                else if (SemiFunc.IsMasterClientOrSingleplayer() && !McNet.HasMod(hit.Player) && hit.Player.playerHealth)
                    hit.Player.playerHealth.HurtOther(hp, Vector3.zero, false);
                McAssets.Play("bowhit", point, 1f, 1.2f / (Random.value * 0.2f + 0.9f));
                Kill();
                return;
            }
            if (hit.Block is TntBlock tnt && onFire) tnt.Prime(4f);
            var rb = hit.Ray.rigidbody;
            if (rb && !rb.isKinematic && SemiFunc.IsMasterClientOrSingleplayer()) rb.AddForceAtPosition(dir * 0.6f, point, ForceMode.Impulse);

            // stick in it, tip buried a little
            stuck = true;
            age = 0f;
            transform.SetPositionAndRotation(point - dir * B * 0.25f, Quaternion.LookRotation(dir));
            stuckTo = hit.Ray.collider.transform;
            hadStuckTo = true;
            stuckPos = stuckTo.InverseTransformPoint(transform.position);
            stuckRot = Quaternion.Inverse(stuckTo.rotation) * transform.rotation;
            McAssets.Play("bowhit", point, 1f, 1.2f / (Random.value * 0.2f + 0.9f));
        }

        void StuckUpdate()
        {
            if (stuckTo) transform.SetPositionAndRotation(stuckTo.TransformPoint(stuckPos), stuckTo.rotation * stuckRot);
            else if (hadStuckTo) { stuck = false; hadStuckTo = false; vel = Vector3.zero; return; }
            if (age > 60f) { Kill(); return; }
            if (!pickup || age < 0.5f) return;
            var p = McPlayer.Instance;
            bool meIn = p && p.Active && InRange(p.FeetPosition, 0f) && p.Inventory.HasRoomFor(ItemStack.Mc("arrow"));
            if (!McNet.Resolves(Owner))
            {
                if (meIn && (claimTimer -= Time.deltaTime) <= 0f) { claimTimer = 0.2f; McNet.SendArrowClaim(this); }
                return;
            }
            int best = McNet.Nobody;
            float bestD = float.MaxValue;
            if (meIn) { best = McNet.LocalActor; bestD = (p.FeetPosition - transform.position).sqrMagnitude; }
            foreach (var kv in claims)
            {
                if (Time.time - kv.Value > 0.5f) continue;
                var pa = McNet.AvatarOf(kv.Key);
                if (!pa || pa.isDisabled || pa.deadSet || !InRange(pa.transform.position, 1.5f)) continue;
                float dist = (pa.transform.position - transform.position).sqrMagnitude;
                if (dist < bestD) { bestD = dist; best = kv.Key; }
            }
            if (best == McNet.Nobody) return;
            McNet.SendArrowPickup(this, best);
            GiveTo(best);
        }

        bool InRange(Vector3 feet, float slack)
        {
            var d = transform.position - feet;
            return new Vector2(d.x, d.z).magnitude <= 1f + slack && d.y >= -0.5f - slack && d.y <= 2.2f + slack;
        }

        public void AddClaim(int actor) { if (stuck) claims[actor] = Time.time; }

        /// Picked up by `actor`: gone here, and in the local inventory if that's us.
        public void GiveTo(int actor)
        {
            McAssets.Play("pop", transform.position, 0.2f, ((Random.value - Random.value) * 0.7f + 1f) * 2f);
            if (actor == McNet.LocalActor) CollectArrow(transform.position);
            Kill();
        }

        public static void CollectArrow(Vector3 dropAt)
        {
            var s = ItemStack.Mc("arrow");
            var p = McPlayer.Instance;
            if (p && p.Inventory.Add(s)) return;
            ItemEntity.Drop(s, dropAt, Vector3.zero, 1f); // full by now
        }
    }

    /// A thrown ender pearl: flies like a snowball, and where it lands the thrower teleports.
    internal class PearlEntity : MonoBehaviour
    {
        Vector3 vel;
        float age;
        Transform visual;

        public static void Throw(Vector3 pos, Vector3 vel)
        {
            var go = new GameObject("mc_ender_pearl");
            go.transform.position = pos;
            var q = new GameObject("sprite");
            q.transform.SetParent(go.transform, false);
            q.transform.localScale = Vector3.one * McWorld.B * 0.4f;
            var mb = new MeshBuilder();
            mb.Quad(new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0),
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1));
            q.AddComponent<MeshFilter>().sharedMesh = mb.Build("mc_pearl_quad");
            var mr = q.AddComponent<MeshRenderer>();
            mr.sharedMaterial = Mats.SpriteTex(McAssets.EnderPearl);
            mr.shadowCastingMode = ShadowCastingMode.Off;
            var p = go.AddComponent<PearlEntity>();
            p.vel = vel;
            p.visual = q.transform;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            age += dt;
            vel.y -= 12f * McWorld.B * dt;          // 0.03 blocks/tick^2
            vel *= Mathf.Pow(0.99f, dt * 20f);
            var pos = transform.position;
            var step = vel * dt;
            if (McWorld.CastProjectile(pos, step, out var hit))
            {
                Particles.Portal(hit.Ray.point, 32);
                if (hit.Enemy) McWorld.PushEnemy(hit.Enemy, vel.normalized * 1.5f, false); // a pearl hit does no damage
                McWorld.TeleportLocalPlayer(hit.Ray.point, hit.Ray.normal);
                Destroy(gameObject);
                return;
            }
            transform.position = pos + step;
            if (age > 20f || pos.y < -300f) Destroy(gameObject);
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam && visual) visual.rotation = cam.transform.rotation;
        }
    }
}
