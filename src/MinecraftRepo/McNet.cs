using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace MinecraftRepo
{
    /// Multiplayer sync for TNT, primed TNT, torches, fire, arrows and dropped items. One Photon event (no PhotonViews): every entity gets
    /// a net id (creator's actor number + counter), and placing, priming, breaking, exploding and shooting are sent to
    /// the other players, who rebuild the same entity locally. Explosions are decided by the host when it has the mod
    /// (otherwise by whoever lit the TNT); everyone else waits for its Explode so chain reactions match everywhere.
    /// Dropped items are created by one client and given out by one client (see ItemEntity); objects held by other
    /// players stay parked by the host, which is the only one R.E.P.O. lets move them. Each player also tells the others
    /// what is in their hands and what they're doing with it, so their model holds it (HeldRig).
    internal class McNet : MonoBehaviour
    {
        const byte EventCode = 173;   // R.E.P.O. uses 123, 124 and 199; PUN reserves 200+
        const string Magic = "mcr1";
        const string PropKey = "mcrepo";
        public const int Nobody = int.MinValue;

        enum Msg : byte
        {
            PlaceTnt = 1, PlaceTorch, Break, Prime, Explode, Arrow, ArrowGone,
            PlaceFire, Item, ItemClaim, ItemPickup, ItemMerge, ItemGone, ItemMove, ItemPush, ReleaseObject,
            ArrowClaim, ArrowPickup, Hands, Swing, HurtEnemy, Unpack, ArmGrenade, GunFire, GunAim,
            Firework, Bobber, BobberGone, PushEnemy, YankPlayer, PushObject, Charging,
        }

        public static bool Online => GameManager.instance && SemiFunc.IsMultiplayer() && PhotonNetwork.InRoom;
        public static int LocalActor => Online && PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : 0;

        /// The host runs the mod too (it advertises itself through a player property).
        public static bool HostHasMod
            => !Online || PhotonNetwork.IsMasterClient
            || (PhotonNetwork.MasterClient != null && PhotonNetwork.MasterClient.CustomProperties.ContainsKey(PropKey));

        public static bool HasMod(PlayerAvatar pa)
            => pa && pa.photonView && pa.photonView.Owner != null && pa.photonView.Owner.CustomProperties.ContainsKey(PropKey);

        /// Whether this client decides an explosion of TNT lit by `primer`.
        public static bool Resolves(int primer) => !Online || (HostHasMod ? PhotonNetwork.IsMasterClient : primer == LocalActor);

        static int counter;
        static readonly Dictionary<int, McBlock> blocks = new Dictionary<int, McBlock>();
        static readonly Dictionary<int, PrimedTnt> primed = new Dictionary<int, PrimedTnt>();
        static readonly Dictionary<int, ArrowEntity> arrows = new Dictionary<int, ArrowEntity>();
        static readonly Dictionary<int, ItemEntity> items = new Dictionary<int, ItemEntity>();
        static readonly HashSet<int> exploded = new HashSet<int>();
        static readonly HashSet<int> goneItems = new HashSet<int>(); // picked up or merged before their drop arrived
        static readonly Dictionary<int, RemoteHands> hands = new Dictionary<int, RemoteHands>();
        object levelSeen;
        bool loggedHost, subscribed;
        float propRetry;

        public static int NewId()
        {
            counter = (counter + 1) & 0xFFFFF;
            if (counter == 0) counter = 1;
            return ((LocalActor & 0x7FF) << 20) | counter;
        }

        void OnDisable()
        {
            if (subscribed) PhotonNetwork.NetworkingClient.EventReceived -= OnEvent;
            subscribed = false;
        }

        void Update()
        {
            // subscribed once the game is running rather than at plugin load, before R.E.P.O. has set Photon up
            if (!subscribed && PhotonNetwork.NetworkingClient != null)
            {
                PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
                subscribed = true;
            }
            // a new level: every entity of the last one was destroyed with its scene
            if (!ReferenceEquals(levelSeen, LevelGenerator.Instance))
            {
                levelSeen = LevelGenerator.Instance;
                blocks.Clear(); primed.Clear(); arrows.Clear(); items.Clear(); exploded.Clear(); goneItems.Clear();
                hands.Clear(); // their objects went with the level; everyone resends within 2 s
                RemoteBobber.Clear();
                handsSent = null;
                loggedHost = false;
            }
            Durability.StationTick();
            if (!Online) { InventorySave.Offline(); return; }
            if (PhotonNetwork.IsMasterClient) InventorySave.Publish();
            else InventorySave.Follow();
            var me = PhotonNetwork.LocalPlayer;
            // tells the others this player has the mod (it shows up locally once the server echoes it back)
            if (me != null && !me.CustomProperties.ContainsKey(PropKey) && (propRetry -= Time.unscaledDeltaTime) <= 0f)
            {
                propRetry = 3f;
                me.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { PropKey, Plugin.Version } });
            }
            if (!loggedHost && InLevel)
            {
                loggedHost = true;
                Plugin.Log.LogInfo("net: " + (PhotonNetwork.IsMasterClient ? "hosting" : HostHasMod ? "host has the mod" : "host doesn't have the mod, TNT is decided by whoever lights it"));
            }
            if (InLevel) SendHands();
        }

        static bool InLevel => LevelGenerator.Instance && LevelGenerator.Instance.Generated;

        // ---------- registry ----------

        public static void Register(McBlock b) { if (b && b.NetId != 0) blocks[b.NetId] = b; }
        public static void Register(PrimedTnt p) { if (p && p.NetId != 0) primed[p.NetId] = p; }
        public static void Register(ArrowEntity a) { if (a && a.NetId != 0) arrows[a.NetId] = a; }
        public static void Register(ItemEntity e) { if (e && e.NetId != 0) items[e.NetId] = e; }

        public static void Forget(McBlock b) { if (b.NetId != 0 && blocks.TryGetValue(b.NetId, out var x) && x == b) blocks.Remove(b.NetId); }
        public static void Forget(PrimedTnt p) { if (p.NetId != 0 && primed.TryGetValue(p.NetId, out var x) && x == p) primed.Remove(p.NetId); }
        public static void Forget(ArrowEntity a) { if (a.NetId != 0 && arrows.TryGetValue(a.NetId, out var x) && x == a) arrows.Remove(a.NetId); }
        public static void Forget(ItemEntity e) { if (e.NetId != 0 && items.TryGetValue(e.NetId, out var x) && x == e) items.Remove(e.NetId); }

        static T Find<T>(Dictionary<int, T> d, int id) where T : Object => id != 0 && d.TryGetValue(id, out var x) && x ? x : null;

        public static bool IsPrimedOrGone(int id) => exploded.Contains(id) || Find(primed, id);
        public static void MarkExploded(int id) { if (id != 0) exploded.Add(id); }

        // ---------- local actions (do it here, tell the others) ----------

        public static TntBlock PlaceTnt(Vector3 center, Quaternion rot)
        {
            var b = TntBlock.Place(center, rot);
            b.NetId = NewId();
            b.Owner = LocalActor;
            Register(b);
            Send(Msg.PlaceTnt, b.NetId, center, rot);
            return b;
        }

        public static TorchBlock PlaceTorch(Vector3 point, Vector3 normal, McBlock support)
        {
            var b = TorchBlock.Place(point, normal, support);
            b.NetId = NewId();
            b.Owner = LocalActor;
            Register(b);
            Send(Msg.PlaceTorch, b.NetId, point, normal, support ? support.NetId : 0);
            return b;
        }

        /// Flint and steel's fire; it burns out at the same time everywhere.
        public static FireBlock PlaceFire(Vector3 floor)
        {
            var f = FireBlock.Place(floor);
            if (!f) return null;
            f.NetId = NewId();
            Register(f);
            Send(Msg.PlaceFire, f.NetId, floor, f.Life, PhotonNetwork.Time);
            return f;
        }

        /// sound: 1 = bow, 2 = crossbow (played by the others at the arrow; the shooter plays its own).
        public static void ShootArrow(Vector3 pos, Vector3 vel, bool crit, bool pickup, byte sound)
        {
            var a = ArrowEntity.Shoot(pos, vel, crit, pickup, false, null);
            a.NetId = NewId();
            a.Owner = LocalActor;
            Register(a);
            Send(Msg.Arrow, a.NetId, pos, vel, crit, pickup, sound, PhotonNetwork.Time);
        }

        public static void SendBreak(McBlock b, bool effects) { if (b.NetId != 0) Send(Msg.Break, b.NetId, effects); }

        public static void SendPrime(int id, float fuse, float angle, Vector3 pos, Quaternion rot, int primer)
        {
            if (id != 0) Send(Msg.Prime, id, fuse, angle, pos, rot, primer, PhotonNetwork.Time);
        }

        public static void SendExplode(int id, Vector3 pos) { if (id != 0) Send(Msg.Explode, id, pos); }

        public static void SendArrowClaim(ArrowEntity a)
        {
            if (a.NetId == 0 || !Online) return;
            SendTo(HostHasMod ? PhotonNetwork.MasterClient.ActorNumber : a.Owner, Msg.ArrowClaim, a.NetId);
        }

        public static void SendArrowPickup(ArrowEntity a, int actor) { if (a.NetId != 0) Send(Msg.ArrowPickup, a.NetId, actor); }

        // ---------- dropped items ----------

        public static void SendItem(ItemEntity e, Vector3 vel, float pickupDelay)
            => Send(Msg.Item, e.NetId, WriteStack(e.Stack), e.transform.position, vel, pickupDelay);

        /// Asks the deciding client for the item (sent while the local player stands on it with room for it).
        public static void SendItemClaim(ItemEntity e)
        {
            if (e.NetId == 0 || !Online) return;
            int to = HostHasMod ? PhotonNetwork.MasterClient.ActorNumber : e.Owner;
            SendTo(to, Msg.ItemClaim, e.NetId);
        }

        /// The deciding client gives it to `actor`, with the stack they get.
        public static void SendItemPickup(ItemEntity e, int actor) { if (e.NetId != 0) Send(Msg.ItemPickup, e.NetId, actor, WriteStack(e.Stack)); }
        public static void SendItemMerge(ItemEntity keep, ItemEntity gone) { if (keep.NetId != 0) Send(Msg.ItemMerge, keep.NetId, gone.NetId, WriteStack(keep.Stack)); }
        public static void SendItemGone(ItemEntity e) { if (e.NetId != 0) Send(Msg.ItemGone, e.NetId); }
        public static void SendItemMove(ItemEntity e) { if (e.NetId != 0) Send(Msg.ItemMove, e.NetId, e.transform.position); }
        public static void SendItemPush(ItemEntity e, Vector3 dv) { if (e.NetId != 0) Send(Msg.ItemPush, e.NetId, dv); }

        /// A client can't move R.E.P.O. objects (OverrideDeactivate and Teleport only act on the host), so the host
        /// takes the object out of storage where the client put it.
        public static void SendReleaseObject(PhysGrabObject o, Vector3 pos, Quaternion rot, Vector3 vel)
        {
            if (!o || !o.photonView || !Online) return;
            Send(Msg.ReleaseObject, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, o.photonView.ViewID, pos, rot, vel);
        }

        /// Enemies live on the host (EnemyHealth.Hurt only acts there): a client's melee hit is dealt by the host, which also
        /// drains the R.E.P.O. weapon's battery when the hit lands (`melee`: the weapon, or null).
        public static void SendHurtEnemy(Enemy e, float mcDamage, Vector3 dir, Vector3 push, float stun, ItemMelee melee)
        {
            if (!e || !e.PhotonView || !Online) return;
            var weapon = melee ? melee.GetComponent<PhotonView>() : null;
            Send(Msg.HurtEnemy, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient },
                e.PhotonView.ViewID, mcDamage, dir, push, stun, weapon ? weapon.ViewID : 0);
        }

        /// A bought Minecraft item a client mined: only the host can take it off the purchases and destroy it.
        public static void SendUnpack(PhysGrabObject o)
        {
            if (!o || !o.photonView || !Online) return;
            Send(Msg.Unpack, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, o.photonView.ViewID);
        }

        /// A grenade a client threw: the host pulls the pin (sent after ReleaseObject; both reliable, so they arrive in order).
        public static void SendArmGrenade(PhysGrabObject o, PlayerAvatar by)
        {
            if (!o || !o.photonView || !Online) return;
            Send(Msg.ArmGrenade, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient },
                o.photonView.ViewID, by && by.photonView ? by.photonView.ViewID : 0);
        }

        /// A shot from a gun in the inventory, with the muzzle pose it fires from (the parked gun's own muzzle is far away).
        /// Everyone points their copy of the gun there; the host also pulls the trigger when a client fired.
        public static void SendGunFire(ItemGun g, Transform muzzle, PlayerAvatar by)
        {
            var pv = g ? g.GetComponent<PhotonView>() : null;
            if (!pv || !Online) return;
            Send(Msg.GunFire, pv.ViewID, muzzle.position, muzzle.rotation, by && by.photonView ? by.photonView.ViewID : 0);
        }

        /// The aim while a gun keeps shooting (a laser); unreliable, 20 a second.
        public static void SendGunAim(ItemGun g, Transform muzzle)
        {
            var pv = g ? g.GetComponent<PhotonView>() : null;
            if (!pv || !Online) return;
            Send(Msg.GunAim, new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendUnreliable,
                pv.ViewID, muzzle.position, muzzle.rotation);
        }

        static ItemGun FindGun(int viewId)
        {
            var pv = viewId != 0 ? PhotonView.Find(viewId) : null;
            return pv ? pv.GetComponent<ItemGun>() : null;
        }

        /// colorIds: palette indices of its star.
        public static void SendFirework(Vector3 pos, Vector3 vel, bool angled, float lifetime, int[] colorIds)
            => Send(Msg.Firework, pos, vel, angled, lifetime, colorIds, PhotonNetwork.Time);

        /// hand: 0 = main hand, 1 = off hand (where the line goes on their model).
        public static void SendBobber(Vector3 pos, int hand)
        {
            if (Online) Send(Msg.Bobber, new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendUnreliable, pos, (byte)hand);
        }

        public static void SendBobberGone() => Send(Msg.BobberGone);

        static readonly RaiseEventOptions ToHost = new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient };

        public static void SendPushEnemy(Enemy e, Vector3 vel, bool yank)
        {
            if (e && e.PhotonView) Send(Msg.PushEnemy, ToHost, e.PhotonView.ViewID, vel, yank);
        }

        public static void SendYankPlayer(PlayerAvatar pa, Vector3 vel)
        {
            if (pa && pa.photonView) Send(Msg.YankPlayer, ToHost, pa.photonView.ViewID, vel);
        }

        public static void SendPushObject(PhysGrabObject o, Vector3 vel)
        {
            if (o && o.photonView) Send(Msg.PushObject, ToHost, o.photonView.ViewID, vel);
        }

        /// mc: Minecraft items charging; ids: R.E.P.O. battery items for the host to charge.
        public static void SendCharging(int mc, int[] ids)
        {
            if (Online) Send(Msg.Charging, ToHost, SendOptions.SendUnreliable, mc, ids);
        }

        // ---------- what each player holds ----------

        /// Another modded player's hands as they last sent them.
        class RemoteHands
        {
            public readonly HandsState State = new HandsState();
            public readonly object[][] Raw = new object[2][];
            public readonly float[] SwingStart = { -10f, -10f }; // Time.time when each arm's swing arrived
            public double UseStart;
            public bool Active;
            public float RetryAt;
        }

        readonly HandsState handsNow = new HandsState();
        readonly float[] lastSwing = { -1f, -1f };
        string handsSent;
        float handsResendAt;

        /// Hands (main, off hand: a stack or empty), the hand in use + 1, the use (HandsState.Use*), when it started
        /// (PhotonNetwork.Time), flags (1 = mod active, 2 = flashlight on, 4/8 = main/off fishing rod cast). Sent when any of it
        /// changes and every 2 s for players who load in later. Swings (Swing: hand) go unreliably.
        void SendHands()
        {
            var h = handsNow;
            h.FillLocal();
            var p = McPlayer.Instance;
            byte flags = (byte)((p && p.Active ? 1 : 0) | (h.FlashlightOn ? 2 : 0) | (h.Cast[0] ? 4 : 0) | (h.Cast[1] ? 8 : 0));
            string sig = StackSig(h.Hands[0]) + "/" + StackSig(h.Hands[1]) + "/" + h.UsingHand + "/" + h.UseKind + "/" + flags;
            if (sig != handsSent || Time.unscaledTime >= handsResendAt)
            {
                handsSent = sig;
                handsResendAt = Time.unscaledTime + 2f;
                double start = h.UsingHand >= 0 ? PhotonNetwork.Time - h.UseSeconds : 0.0;
                Send(Msg.Hands, HandArg(h.Hands[0]), HandArg(h.Hands[1]), (byte)(h.UsingHand + 1), h.UseKind, start, flags);
            }
            for (int i = 0; i < 2; i++)
            {
                float s = h.Swing[i];
                if (s >= 0f && (lastSwing[i] < 0f || s < lastSwing[i]))
                    Send(Msg.Swing, new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendUnreliable, (byte)i);
                lastSwing[i] = s;
            }
        }

        static string StackSig(ItemStack s)
        {
            if (s == null) return "-";
            var top = s.IsVirtual || s.Objects.Count == 0 ? null : s.Objects[s.Objects.Count - 1];
            return (int)s.Kind + ":" + s.Key + ":" + s.Id + ":" + (top && top.photonView ? top.photonView.ViewID : 0) + ":" + s.Loaded;
        }

        static object[] HandArg(ItemStack s) => s == null ? new object[0] : WriteStack(s);

        /// Null when empty, or when its R.E.P.O. object doesn't exist here (yet).
        static ItemStack ReadHand(object[] a)
        {
            if (a == null || a.Length == 0) return null;
            var s = ReadStack(a);
            return s.Count > 0 ? s : null;
        }

        /// What another player holds (null when they don't have the mod or haven't said yet); `active`: the mod drives
        /// that player (their R.E.P.O. flashlight is then an item).
        public static HandsState HandsOf(PlayerAvatar pa, out bool active)
        {
            active = false;
            if (!Online || !pa || !pa.photonView || !hands.TryGetValue(pa.photonView.OwnerActorNr, out var r)) return null;
            active = r.Active;
            var h = r.State;
            if (Time.time >= r.RetryAt)
            {
                // an object that wasn't spawned here yet when the message came, or a stale one
                r.RetryAt = Time.time + 1f;
                for (int i = 0; i < 2; i++)
                {
                    var s = h.Hands[i];
                    bool stale = s != null && !s.IsVirtual && (s.Objects.Count == 0 || !s.Objects[s.Objects.Count - 1]);
                    if (stale || (s == null && r.Raw[i] != null && r.Raw[i].Length > 0)) h.Hands[i] = ReadHand(r.Raw[i]);
                }
            }
            h.UseSeconds = h.UsingHand >= 0 ? Mathf.Max(0f, (float)(PhotonNetwork.Time - r.UseStart)) : -1f;
            for (int i = 0; i < 2; i++)
            {
                float t = Time.time - r.SwingStart[i];
                h.Swing[i] = t < McPlayer.SwingDuration ? t : -1f;
            }
            return h;
        }

        /// kind, key, name, id, count, damage, single, loaded, object view ids
        static object[] WriteStack(ItemStack s)
        {
            var ids = new int[s.Objects.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = s.Objects[i] && s.Objects[i].photonView ? s.Objects[i].photonView.ViewID : 0;
            return new object[] { (byte)s.Kind, s.Key ?? "", s.Name ?? "", s.Id ?? "", s.VCount, s.Damage, s.Single, s.Loaded ?? "", ids };
        }

        static ItemStack ReadStack(object[] a)
        {
            string Str(object o) => string.IsNullOrEmpty(o as string) ? null : (string)o;
            var s = new ItemStack
            {
                Kind = (ItemKind)(byte)a[0], Key = (string)a[1], Name = (string)a[2], Id = Str(a[3]),
                VCount = (int)a[4], Damage = (int)a[5], Single = (bool)a[6], Loaded = Str(a[7]),
            };
            foreach (int v in (int[])a[8])
            {
                var o = FindObject(v);
                if (o) s.Objects.Add(o);
            }
            if (s.IsVirtual) s.Objects.Clear();
            else s.VCount = 1;
            return s;
        }

        static PhysGrabObject FindObject(int viewId)
        {
            var pv = viewId != 0 ? PhotonView.Find(viewId) : null;
            return pv ? pv.GetComponent<PhysGrabObject>() : null;
        }

        static void Send(Msg kind, params object[] args) => Send(kind, new RaiseEventOptions { Receivers = ReceiverGroup.Others }, args);

        static void SendTo(int actor, Msg kind, params object[] args)
            => Send(kind, new RaiseEventOptions { TargetActors = new[] { actor } }, args);

        static void Send(Msg kind, RaiseEventOptions options, params object[] args) => Send(kind, options, SendOptions.SendReliable, args);

        static void Send(Msg kind, RaiseEventOptions options, SendOptions send, params object[] args)
        {
            if (!Online) return;
            var data = new object[args.Length + 2];
            data[0] = Magic;
            data[1] = (byte)kind;
            args.CopyTo(data, 2);
            PhotonNetwork.RaiseEvent(EventCode, data, options, send);
        }

        // ---------- received ----------

        void OnEvent(EventData ev)
        {
            if (ev.Code != EventCode || !(ev.CustomData is object[] d) || d.Length < 2 || !(d[0] is string m) || m != Magic) return;
            if (!InLevel) return; // R.E.P.O. starts a level only when every player has generated it
            try { Handle((Msg)(byte)d[1], d, ev.Sender); }
            catch (System.Exception e) { Plugin.Log.LogWarning("net: bad message " + d[1] + " from " + ev.Sender + ": " + e.Message); }
        }

        static void Handle(Msg kind, object[] d, int sender)
        {
            switch (kind)
            {
                case Msg.PlaceTnt:
                {
                    int id = (int)d[2];
                    if (Find(blocks, id) || IsPrimedOrGone(id)) return;
                    var pos = (Vector3)d[3];
                    var b = TntBlock.Place(pos, (Quaternion)d[4]);
                    b.NetId = id;
                    b.Owner = sender;
                    Register(b);
                    McAssets.Play("grass", pos, 1f, 0.8f);
                    break;
                }
                case Msg.PlaceTorch:
                {
                    int id = (int)d[2], supportId = (int)d[5];
                    if (Find(blocks, id)) return;
                    var support = Find(blocks, supportId);
                    if (supportId != 0 && !support) return; // its block is already gone: it would have popped off
                    var point = (Vector3)d[3];
                    var b = TorchBlock.Place(point, (Vector3)d[4], support);
                    b.NetId = id;
                    b.Owner = sender;
                    Register(b);
                    McAssets.Play("wood", point, 1f, 0.8f);
                    break;
                }
                case Msg.Break:
                {
                    var b = Find(blocks, (int)d[2]);
                    if (!b) return;
                    if ((bool)d[3]) b.BreakEffects();
                    b.Break(false); // the drop is shared: the client that broke it made it
                    break;
                }
                case Msg.Prime:
                {
                    int id = (int)d[2];
                    if (IsPrimedOrGone(id)) return;
                    float lag = Mathf.Clamp((float)(PhotonNetwork.Time - (double)d[8]), 0f, 1f);
                    float fuse = Mathf.Max(0.05f, (float)d[3] - lag);
                    var b = Find(blocks, id) as TntBlock;
                    if (b) b.PrimeLocal(fuse, (float)d[4], (int)d[7]);
                    else PrimedTnt.Spawn(id, (Vector3)d[5], (Quaternion)d[6], fuse, (float)d[4], (int)d[7]);
                    break;
                }
                case Msg.Explode:
                {
                    int id = (int)d[2];
                    if (exploded.Contains(id)) return;
                    exploded.Add(id);
                    var p = Find(primed, id);
                    if (p) p.Remove();
                    Explosion.Explode((Vector3)d[3], 4f, false);
                    break;
                }
                case Msg.Arrow:
                {
                    int id = (int)d[2];
                    if (Find(arrows, id)) return;
                    var pos = (Vector3)d[3];
                    var a = ArrowEntity.Shoot(pos, (Vector3)d[4], (bool)d[5], (bool)d[6], true, AvatarOf(sender));
                    a.NetId = id;
                    a.Owner = sender;
                    Register(a);
                    byte sound = (byte)d[7];
                    if (sound == 1) McAssets.Play("throw", pos, 1f, 1f / (Random.value * 0.4f + 1.2f) + 0.5f);
                    else if (sound == 2) McAssets.Play("crossbow_shoot", pos, 1f, 1f);
                    // catch up to where it is on the shooter's screen
                    float lag = Mathf.Clamp((float)(PhotonNetwork.Time - (double)d[8]), 0f, 0.5f);
                    while (lag > 0f && !a.Gone)
                    {
                        float dt = Mathf.Min(0.02f, lag);
                        a.Step(dt);
                        lag -= dt;
                    }
                    break;
                }
                case Msg.ArrowGone:
                {
                    var a = Find(arrows, (int)d[2]);
                    if (a) Destroy(a.gameObject);
                    break;
                }
                case Msg.ArrowClaim:
                {
                    int id = (int)d[2];
                    var a = Find(arrows, id);
                    if (a) a.AddClaim(sender);
                    else SendTo(sender, Msg.ArrowGone, id); // it's gone here (picked up, or it hit something): theirs is a leftover
                    break;
                }
                case Msg.ArrowPickup:
                {
                    var a = Find(arrows, (int)d[2]);
                    int actor = (int)d[3];
                    if (a) a.GiveTo(actor);
                    else if (actor == LocalActor) ArrowEntity.CollectArrow(McPlayer.Instance ? McPlayer.Instance.FeetPosition : Vector3.zero);
                    break;
                }
                case Msg.PlaceFire:
                {
                    int id = (int)d[2];
                    if (Find(blocks, id)) return;
                    float lag = Mathf.Clamp((float)(PhotonNetwork.Time - (double)d[5]), 0f, 1f);
                    var f = FireBlock.Place((Vector3)d[3], Mathf.Max(0.1f, (float)d[4] - lag));
                    if (!f) return; // a fire already burns there
                    f.NetId = id;
                    f.Owner = sender;
                    Register(f);
                    break;
                }
                case Msg.Item:
                {
                    int id = (int)d[2];
                    if (Find(items, id) || goneItems.Contains(id)) return;
                    var stack = ReadStack((object[])d[3]);
                    if (stack.Count <= 0) return; // its objects are gone here
                    var e = ItemEntity.Spawn(stack, (Vector3)d[4], (Vector3)d[5], (float)d[6]);
                    e.NetId = id;
                    e.Owner = sender;
                    Register(e);
                    break;
                }
                case Msg.ItemClaim:
                {
                    int id = (int)d[2];
                    var e = Find(items, id);
                    if (e) { if (e.Authority) e.AddClaim(sender); }
                    else SendTo(sender, Msg.ItemGone, id); // already gone here: theirs is a leftover
                    break;
                }
                case Msg.ItemPickup:
                {
                    var e = Find(items, (int)d[2]);
                    if (!e) goneItems.Add((int)d[2]);
                    int actor = (int)d[3];
                    var stack = actor == LocalActor ? ReadStack((object[])d[4]) : null;
                    if (e) e.Take(actor, stack);
                    else if (stack != null) ItemEntity.Collect(stack, McPlayer.Instance ? McPlayer.Instance.FeetPosition : Vector3.zero);
                    break;
                }
                case Msg.ItemMerge:
                {
                    var keep = Find(items, (int)d[2]);
                    var gone = Find(items, (int)d[3]);
                    if (gone) Destroy(gone.gameObject);
                    else goneItems.Add((int)d[3]);
                    if (keep)
                    {
                        var s = ReadStack((object[])d[4]);
                        if (s.Count > 0) keep.Stack = s;
                    }
                    break;
                }
                case Msg.ItemGone:
                {
                    var e = Find(items, (int)d[2]);
                    if (e) Destroy(e.gameObject);
                    else goneItems.Add((int)d[2]);
                    break;
                }
                case Msg.ItemMove:
                {
                    var e = Find(items, (int)d[2]);
                    if (e) e.SetRest((Vector3)d[3]);
                    break;
                }
                case Msg.ItemPush:
                {
                    var e = Find(items, (int)d[2]);
                    if (e) e.Push((Vector3)d[3]);
                    break;
                }
                case Msg.ReleaseObject:
                {
                    if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
                    var o = FindObject((int)d[2]);
                    if (o) Storage.Release(o, (Vector3)d[3], (Quaternion)d[4], (Vector3)d[5]);
                    break;
                }
                case Msg.HurtEnemy:
                {
                    if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
                    var pv = PhotonView.Find((int)d[2]);
                    var e = pv ? pv.GetComponent<Enemy>() : null;
                    if (!McWorld.HurtEnemy(e, (float)d[3], (Vector3)d[4], (Vector3)d[5], (float)d[6])) return;
                    int weapon = (int)d[7];
                    var wpv = weapon != 0 ? PhotonView.Find(weapon) : null;
                    var melee = wpv ? wpv.GetComponent<ItemMelee>() : null;
                    if (melee) RepoWeapons.Wear(melee);
                    break;
                }
                case Msg.Unpack:
                {
                    if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
                    var o = FindObject((int)d[2]);
                    if (o) McShop.Unpack(o);
                    break;
                }
                case Msg.ArmGrenade:
                {
                    if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
                    var o = FindObject((int)d[2]);
                    int by = (int)d[3];
                    var pv = by != 0 ? PhotonView.Find(by) : null;
                    if (o) McPlayer.ArmGrenade(o, pv ? pv.GetComponent<PlayerAvatar>() : null);
                    break;
                }
                case Msg.GunFire:
                {
                    var g = FindGun((int)d[2]);
                    if (!g) return;
                    RepoWeapons.AimRemote(g, (Vector3)d[3], (Quaternion)d[4]);
                    if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
                    int by = (int)d[5];
                    var pv = by != 0 ? PhotonView.Find(by) : null;
                    RepoWeapons.Pull(g, pv ? pv.GetComponent<PlayerAvatar>() : null);
                    break;
                }
                case Msg.GunAim:
                {
                    var g = FindGun((int)d[2]);
                    if (g) RepoWeapons.AimRemote(g, (Vector3)d[3], (Quaternion)d[4]);
                    break;
                }
                case Msg.Firework:
                {
                    var f = FireworkEntity.Spawn((Vector3)d[2], (Vector3)d[3], (bool)d[4], (float)d[5], (int[])d[6], AvatarOf(sender), true);
                    // catch up to where it is on the shooter's screen
                    float lag = Mathf.Clamp((float)(PhotonNetwork.Time - (double)d[7]), 0f, 0.5f);
                    while (lag > 0f && !f.Gone)
                    {
                        float dt = Mathf.Min(0.02f, lag);
                        f.Step(dt);
                        lag -= dt;
                    }
                    break;
                }
                case Msg.Bobber:
                    RemoteBobber.Set(sender, (Vector3)d[2], (byte)d[3]);
                    break;
                case Msg.BobberGone:
                    RemoteBobber.Remove(sender);
                    break;
                case Msg.PushEnemy:
                {
                    if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
                    var pv = PhotonView.Find((int)d[2]);
                    var e = pv ? pv.GetComponent<Enemy>() : null;
                    if (e) McWorld.PushEnemy(e, (Vector3)d[3], (bool)d[4]);
                    break;
                }
                case Msg.YankPlayer:
                {
                    if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
                    var pv = PhotonView.Find((int)d[2]);
                    var pa = pv ? pv.GetComponent<PlayerAvatar>() : null;
                    if (pa) McWorld.YankPlayer(pa, (Vector3)d[3]);
                    break;
                }
                case Msg.PushObject:
                {
                    if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
                    var o = FindObject((int)d[2]);
                    if (o) McWorld.PushObject(o, (Vector3)d[3]);
                    break;
                }
                case Msg.Charging:
                    if (SemiFunc.IsMasterClientOrSingleplayer()) Durability.OnRemoteCharging(sender, (int)d[2], (int[])d[3]);
                    break;
                case Msg.Hands:
                {
                    if (!hands.TryGetValue(sender, out var r)) hands[sender] = r = new RemoteHands();
                    for (int i = 0; i < 2; i++)
                    {
                        r.Raw[i] = (object[])d[2 + i];
                        r.State.Hands[i] = ReadHand(r.Raw[i]);
                    }
                    r.State.UsingHand = (byte)d[4] - 1;
                    r.State.UseKind = (byte)d[5];
                    r.UseStart = (double)d[6];
                    byte f = (byte)d[7];
                    r.Active = (f & 1) != 0;
                    r.State.FlashlightOn = (f & 2) != 0;
                    r.State.Cast[0] = (f & 4) != 0;
                    r.State.Cast[1] = (f & 8) != 0;
                    r.RetryAt = Time.time + 1f;
                    break;
                }
                case Msg.Swing:
                {
                    if (hands.TryGetValue(sender, out var r)) r.SwingStart[(byte)d[2] & 1] = Time.time;
                    break;
                }
            }
        }

        public static PlayerAvatar AvatarOf(int actor)
        {
            if (!GameDirector.instance) return null;
            foreach (var pa in GameDirector.instance.PlayerList)
                if (pa && pa.photonView && pa.photonView.OwnerActorNr == actor) return pa;
            return null;
        }
    }
}
