using HarmonyLib;

namespace MinecraftRepo
{
    /// Remaps R.E.P.O.'s input while the Minecraft player is active:
    /// LMB no longer grabs (it mines/attacks), RMB grabs unless it placed a block, R rotates the grabbed object,
    /// 1-9 belong to the hotbar, Q drops the held stack, X tumbles, E opens the inventory, Esc closes it.
    internal static class McInput
    {
        /// mode: 0 = down, 1 = hold, 2 = up
        public static bool Override(InputKey key, int mode, out bool result)
        {
            result = false;
            var p = McPlayer.Instance;
            if (p == null || !p.Active) return false;

            // Esc closes the inventory only; the pause menu opens when no Minecraft screen was open
            if ((key == InputKey.Menu || key == InputKey.Back) && (p.InventoryOpen || p.InventoryClosedFrame >= UnityEngine.Time.frameCount - 1))
                return true;

            if (p.InventoryOpen)
            {
                switch (key)
                {
                    case InputKey.Chat:
                    case InputKey.Confirm:
                    case InputKey.PushToTalk:
                    case InputKey.ToggleMute:
                        return false;
                    default:
                        return true; // everything else is swallowed by the inventory screen
                }
            }

            var kb = UnityEngine.InputSystem.Keyboard.current;
            switch (key)
            {
                case InputKey.Grab:
                    result = mode == 0 ? p.GrabDown() : mode == 1 ? p.GrabHold() : p.GrabUp();
                    return true;
                case InputKey.Rotate:
                    if (kb == null) return true;
                    result = mode == 0 ? kb.rKey.wasPressedThisFrame : mode == 1 ? kb.rKey.isPressed : kb.rKey.wasReleasedThisFrame;
                    return true;
                case InputKey.Inventory1:
                case InputKey.Inventory2:
                case InputKey.Inventory3:
                case InputKey.Expression1:
                case InputKey.Expression2:
                case InputKey.Expression3:
                case InputKey.Expression4:
                case InputKey.Expression5:
                case InputKey.Expression6:
                    return true;
                case InputKey.Tumble:
                    // Q is Minecraft's drop; tumble moves to X
                    if (kb == null) return true;
                    result = mode == 0 ? kb.xKey.wasPressedThisFrame : mode == 1 ? kb.xKey.isPressed : kb.xKey.wasReleasedThisFrame;
                    return true;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.KeyDown))]
    internal static class KeyDownPatch
    {
        static bool Prefix(InputKey key, ref bool __result)
        {
            if (!McInput.Override(key, 0, out var r)) return true;
            __result = r;
            return false;
        }
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.KeyHold))]
    internal static class KeyHoldPatch
    {
        static bool Prefix(InputKey key, ref bool __result)
        {
            if (!McInput.Override(key, 1, out var r)) return true;
            __result = r;
            return false;
        }
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.KeyUp))]
    internal static class KeyUpPatch
    {
        static bool Prefix(InputKey key, ref bool __result)
        {
            if (!McInput.Override(key, 2, out var r)) return true;
            __result = r;
            return false;
        }
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.GetMouseX))]
    internal static class MouseXPatch
    {
        static bool Prefix(ref float __result)
        {
            var p = McPlayer.Instance;
            if (p == null || !p.InventoryOpen) return true;
            __result = 0f;
            return false;
        }
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.GetMouseY))]
    internal static class MouseYPatch
    {
        static bool Prefix(ref float __result)
        {
            var p = McPlayer.Instance;
            if (p == null || !p.InventoryOpen) return true;
            __result = 0f;
            return false;
        }
    }

    /// While R.E.P.O.'s scroll is free (nothing grabbed), the wheel belongs to the hotbar.
    [HarmonyPatch(typeof(InputManager), nameof(InputManager.KeyPullAndPush))]
    internal static class PushPullPatch
    {
        static bool Prefix(ref float __result)
        {
            var p = McPlayer.Instance;
            if (p == null || !p.Active) return true;
            if (PhysGrabber.instance && PhysGrabber.instance.grabbed && !p.InventoryOpen) return true;
            __result = 0f;
            return false;
        }
    }
}

namespace MinecraftRepo
{
    /// A raised shield blocks R.E.P.O. hurt colliders (bites, swings, projectiles) coming from the front.
    /// Instant-kill colliders (pits, crushers) still kill, like Minecraft's void.
    [HarmonyPatch(typeof(HurtCollider), "PlayerHurt")]
    internal static class ShieldHurtColliderPatch
    {
        static bool Prefix(HurtCollider __instance, PlayerAvatar _player)
        {
            var p = McPlayer.Instance;
            if (p == null || !_player || !_player.isLocal || !p.Blocking || __instance.playerKill) return true;
            if (__instance.playerDamage <= 0 && __instance.playerTumbleForce <= 0f && __instance.playerTumbleTime <= 0f) return true;
            var src = __instance.enemyHost ? McWorld.EnemyCenter(__instance.enemyHost)
                : __instance.Collider ? __instance.Collider.bounds.center : __instance.transform.position;
            if (!p.ShieldBlocks(src)) return true;
            p.OnShieldBlock(src, __instance.enemyHost, __instance.playerDamage / UnityEngine.Mathf.Max(0.01f, Plugin.DamageScale.Value));
            return false;
        }
    }

    /// Direct enemy damage that names its enemy is blocked the same way.
    [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.Hurt))]
    internal static class ShieldHurtPatch
    {
        static bool Prefix(PlayerHealth __instance, int damage, int enemyIndex)
        {
            var p = McPlayer.Instance;
            if (p == null || enemyIndex < 0 || !p.Blocking || !__instance.playerAvatar || !__instance.playerAvatar.isLocal) return true;
            var e = SemiFunc.EnemyGetFromIndex(enemyIndex);
            if (!e) return true;
            var src = McWorld.EnemyCenter(e);
            if (!p.ShieldBlocks(src)) return true;
            p.OnShieldBlock(src, e, damage / UnityEngine.Mathf.Max(0.01f, Plugin.DamageScale.Value));
            return false;
        }
    }
}

namespace MinecraftRepo
{
    /// R.E.P.O.'s item prompts (the name of what you look at or hold, a cart's mode, a valuable's value, a shop price) go to
    /// Minecraft's action bar instead of R.E.P.O.'s HUD text while the mod drives the player.
    internal static class ActionBarPatches
    {
        static bool On => McPlayer.Instance != null && McPlayer.Instance.Active;

        [HarmonyPatch(typeof(ItemInfoUI), nameof(ItemInfoUI.ItemInfoText))]
        internal static class Info
        {
            static bool Prefix(ItemAttributes _itemAttributes, string message, bool enemy)
            {
                if (!On) return true;
                // R.E.P.O.'s own rule: while nothing is grabbed, only the item under the crosshair may speak
                var pg = PhysGrabber.instance;
                var looking = pg ? pg.currentlyLookingAtItemAttributes : null;
                if (pg && !pg.grabbed && _itemAttributes && looking && looking != _itemAttributes) return false;
                McHud.ShowAction(McHud.ActionInfo, message, enemy ? new UnityEngine.Color(1f, 0.2f, 0.1f) : UnityEngine.Color.white);
                return false;
            }
        }

        [HarmonyPatch(typeof(ItemInfoExtraUI), nameof(ItemInfoExtraUI.ItemInfoText))]
        internal static class Extra
        {
            static bool Prefix(string message, UnityEngine.Color color)
            {
                if (!On) return true;
                McHud.ShowAction(McHud.ActionExtra, message, color);
                return false;
            }
        }

        static void Value(WorldSpaceUIValue ui, int value, bool cost)
        {
            var text = cost ? "-$" + SemiFunc.DollarGetString(value) + "K" : "$" + SemiFunc.DollarGetString(value);
            var c = cost ? ui.colorCost : ui.colorValue;
            c.a = 1f;
            McHud.ShowAction(McHud.ActionValue, text, c);
        }

        [HarmonyPatch(typeof(WorldSpaceUIValue), nameof(WorldSpaceUIValue.Show), typeof(PhysGrabObject), typeof(int), typeof(bool), typeof(UnityEngine.Vector3))]
        internal static class PhysValue
        {
            static bool Prefix(WorldSpaceUIValue __instance, PhysGrabObject _grabObject, int _value, bool _cost)
            {
                if (!On) return true;
                Value(__instance, _value, _cost);
                // valuables have no item prompt of their own: their name goes with the value
                var val = _grabObject ? _grabObject.GetComponent<ValuableObject>() : null;
                if (val) McHud.ShowAction(McHud.ActionInfo, ItemStack.NameOf(_grabObject, val, null), UnityEngine.Color.white);
                return false;
            }
        }

        [HarmonyPatch(typeof(WorldSpaceUIValue), nameof(WorldSpaceUIValue.Show), typeof(StaticGrabObject), typeof(int), typeof(bool), typeof(UnityEngine.Vector3))]
        internal static class StaticValue
        {
            static bool Prefix(WorldSpaceUIValue __instance, int _value, bool _cost)
            {
                if (!On) return true;
                Value(__instance, _value, _cost);
                return false;
            }
        }
    }
}
