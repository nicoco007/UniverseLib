using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UniverseLib.UI;
using UniverseLib.Utility;
#if CPP
using System.Runtime.InteropServices;
using UniverseLib.Runtime.Il2Cpp;
#if INTEROP
using Il2CppInterop.Runtime;
#else
using UnhollowerBaseLib;
#endif
#endif

namespace UniverseLib.Input
{
    /// <summary>
    /// Blocks game input while any UniverseLib UI is showing.
    /// <br/>- Legacy Input: patches UnityEngine.Input so it returns false/0 for any caller that isn't UniverseLib or uGUI.
    /// <br/>- InputSystem: disables every enabled InputAction (except UniverseLib's own UI actions) and re-enables them when the UI closes.
    /// </summary>
    internal static class InputBlocker
    {
        static readonly Assembly selfAssembly = typeof(InputBlocker).Assembly;
        static readonly Assembly uguiAssembly = typeof(EventSystem).Assembly;

        static int cachedFrame = -1;
        static bool cachedShowing;

        /// <summary>Whether any UniverseLib UI is showing, cached per frame since input methods are called very often.</summary>
        internal static bool UIShowing
        {
            get
            {
                int frame = Time.frameCount;
                if (frame != cachedFrame)
                {
                    cachedFrame = frame;
                    cachedShowing = UniversalUI.AnyUIShowing;
                }
                return cachedShowing;
            }
        }

        /// <summary>Forces <see cref="UIShowing"/> to be re-evaluated (call when a UI is shown or hidden).</summary>
        internal static void Invalidate() => cachedFrame = -1;

        // While > 0, input reads aren't blocked. Raised around UniverseLib's own reads (LegacyInput) and uGUI's
        // (BaseInput, used by our EventSystem's input module). This is what identifies allowed callers on IL2CPP,
        // where callers are native code that a managed stack walk can't see.
        static int allowDepth;
        internal static void BeginAllow() => allowDepth++;
        internal static void EndAllow() { if (allowDepth > 0) allowDepth--; }

        internal static void Init()
        {
            InitLegacyPatches();
            InitBaseInputPatches();
        }

        internal static void Update()
        {
            // Safety net in case an allow scope was left open (e.g. an exception skipped a BaseInput postfix).
            allowDepth = 0;
            Invalidate();

            if (UIShowing)
                FreezeInputSystemActions();
            else
                RestoreInputSystemActions();
        }

        // ~~~~~~~~~~~~ Legacy Input ~~~~~~~~~~~~

        static void InitLegacyPatches()
        {
            Type input = LegacyInput.TInput;
            if (input == null)
                return;

#if CPP
            InitCppLegacyPatches(input);
#else
            InitMonoLegacyPatches(input);
#endif
        }

        static void InitMonoLegacyPatches(Type input)
        {
            MethodInfo block = AccessTools.Method(typeof(InputBlocker), nameof(Prefix_Block));
            MethodInfo blockKeyCode = AccessTools.Method(typeof(InputBlocker), nameof(Prefix_BlockKeyCode));
            MethodInfo blockKeyName = AccessTools.Method(typeof(InputBlocker), nameof(Prefix_BlockKeyName));
            MethodInfo anyKey = AccessTools.Method(typeof(InputBlocker), nameof(Prefix_AnyKey));
            MethodInfo anyKeyDown = AccessTools.Method(typeof(InputBlocker), nameof(Prefix_AnyKeyDown));
            MethodInfo blockVector2 = AccessTools.Method(typeof(InputBlocker), nameof(Prefix_BlockVector2Out));

            Type[] keyCode = { typeof(KeyCode) };
            Type[] str = { typeof(string) };
            Type[] integer = { typeof(int) };

            int patched = 0, total = 0;
            void TryPatch(Type type, string name, Type[] args, MethodInfo prefix, MethodType methodType = MethodType.Normal)
            {
                if (type == null)
                    return;
                total++;
                if (Universe.Patch(type, name, methodType, args, prefix: prefix))
                    patched++;
            }

            // Anything that keyboard or mouse can trigger is blocked; only joystick (incl. legacy XR) input passes through.
            // Named axes/buttons can always be mapped to keys, so they're blocked outright.

            // Public API. On older Unity versions (and IL2CPP) these are the real implementation.
            foreach (string name in new[] { "GetKey", "GetKeyDown", "GetKeyUp" })
            {
                TryPatch(input, name, keyCode, blockKeyCode);
                TryPatch(input, name, str, blockKeyName);
            }
            foreach (string name in new[] { "GetAxis", "GetAxisRaw", "GetButton", "GetButtonDown", "GetButtonUp" })
                TryPatch(input, name, str, block);
            foreach (string name in new[] { "GetMouseButton", "GetMouseButtonDown", "GetMouseButtonUp" })
                TryPatch(input, name, integer, block);
            getKey = BindKeyMethod(input, "GetKey");
            getKeyDown = BindKeyMethod(input, "GetKeyDown");
            TryPatch(input, "anyKey", null, anyKey, MethodType.Getter);
            TryPatch(input, "anyKeyDown", null, anyKeyDown, MethodType.Getter);

            // Unity 6+: the public methods above are tiny wrappers which Mono inlines into game code,
            // so patching them alone does nothing. Patch the methods they forward to as well.
            foreach (string name in new[] { "GetKeyInt", "GetKeyDownInt", "GetKeyUpInt" })
                TryPatch(input, name, keyCode, blockKeyCode);
            TryPatch(input, "get_mouseScrollDelta_Injected", new[] { typeof(Vector2).MakeByRefType() }, blockVector2);

            Type unsafeUtility = ReflectionUtility.GetTypeByName("UnityEngine.Internal.InputUnsafeUtility") ?? ReflectionUtility.GetTypeByName("UnityEngine.InputUnsafeUtility");
            foreach (string name in new[] { "GetKeyString", "GetKeyDownString", "GetKeyUpString" })
                TryPatch(unsafeUtility, name, str, blockKeyName);
            foreach (string name in new[] { "GetAxis", "GetAxisRaw", "GetButton", "GetButtonDown", "GetButtonUp" })
                TryPatch(unsafeUtility, name, str, block);

            Universe.Log($"Input blocking: patched {patched} legacy Input methods");
        }

        /// <summary>
        /// uGUI's input modules read input through BaseInput (virtual methods, so they can't be inlined into the module).
        /// Mark those reads as allowed so clicks/scrolling on UniverseLib's UI keep working. Only our EventSystem is enabled
        /// while the UI is open, so this doesn't let game UI input through.
        /// </summary>
        static void InitBaseInputPatches()
        {
            MethodInfo begin = AccessTools.Method(typeof(InputBlocker), nameof(Prefix_BeginAllow));
            MethodInfo end = AccessTools.Method(typeof(InputBlocker), nameof(Postfix_EndAllow));
            Type[] integer = { typeof(int) };
            Type[] str = { typeof(string) };

            int patched = 0;
            void TryPatch(string name, Type[] args, MethodType type = MethodType.Normal)
            {
                if (Universe.Patch(typeof(BaseInput), name, type, args, prefix: begin, postfix: end))
                    patched++;
            }

            TryPatch("GetMouseButton", integer);
            TryPatch("GetMouseButtonDown", integer);
            TryPatch("GetMouseButtonUp", integer);
            TryPatch("mouseScrollDelta", null, MethodType.Getter);
            TryPatch("GetAxisRaw", str);
            TryPatch("GetButtonDown", str);

            Universe.Log($"Input blocking: allowed {patched} uGUI BaseInput methods");
        }

        static void Prefix_BeginAllow() => BeginAllow();
        static void Postfix_EndAllow() => EndAllow();

        /// <summary>Returns true if game input should be blocked for the current caller.</summary>
        static bool ShouldBlock()
        {
            if (!UIShowing || allowDepth > 0)
                return false;

#if CPP
            // Callers are native IL2CPP code, so there's nothing useful to walk; allowed callers are marked via allowDepth.
            return true;
#else
            // Also allow UniverseLib itself (and its users like UnityExplorer, which go through InputManager) and uGUI's BaseInput,
            // which our own EventSystem uses. Skip frames belonging to this class, since our prefix is always on the stack.
            StackFrame[] frames = new StackTrace(1, false).GetFrames();
            if (frames == null)
                return true;

            foreach (StackFrame frame in frames)
            {
                Type declaringType = frame.GetMethod()?.DeclaringType;
                if (declaringType == null || declaringType == typeof(InputBlocker))
                    continue;

                Assembly asm = declaringType.Assembly;
                if (asm == selfAssembly || asm == uguiAssembly)
                    return false;
            }

            return true;
#endif
        }

        // ~~~~~~~~~~~~ Which inputs count as keyboard/mouse ~~~~~~~~~~~~

        // KeyCode values from JoystickButton0 onwards are joystick buttons (incl. XR controllers on legacy input).
        // Mouse0-Mouse6 sit just below them, so everything before JoystickButton0 is keyboard or mouse.
        static bool IsKeyboardOrMouse(KeyCode key) => key != KeyCode.None && key < KeyCode.JoystickButton0;

        // Key names as accepted by Input.GetKey(string), e.g. "space", "mouse 0", "joystick 1 button 0".
        static bool IsKeyboardOrMouse(string keyName)
            => !string.IsNullOrEmpty(keyName) && !keyName.StartsWith("joystick", StringComparison.OrdinalIgnoreCase);

        // "Any joystick" buttons (JoystickButton0-19). While blocked, anyKey/anyKeyDown only report these, so a
        // "press any button" on a controller still works but keyboard/mouse can't trigger it.
        const int FirstAnyJoystickButton = (int)KeyCode.JoystickButton0;
        const int LastAnyJoystickButton = (int)KeyCode.JoystickButton19;

        static bool AnyJoystickButton(Func<int, bool> getKey)
        {
            if (getKey == null)
                return false;
            for (int key = FirstAnyJoystickButton; key <= LastAnyJoystickButton; key++)
            {
                if (getKey(key))
                    return true;
            }
            return false;
        }

        // Works for bool and float methods: skipping the original leaves __result as false / 0.
        static bool Prefix_Block() => !ShouldBlock();
        static bool Prefix_BlockKeyCode(KeyCode __0) => !(IsKeyboardOrMouse(__0) && ShouldBlock());
        static bool Prefix_BlockKeyName(string __0) => !(IsKeyboardOrMouse(__0) && ShouldBlock());

        // Input.GetKey/GetKeyDown(KeyCode), bound once (UniverseLib only accesses legacy Input through reflection).
        // Joystick KeyCodes aren't blocked by the GetKey patches, so these return the real value.
        static Func<KeyCode, bool> getKey, getKeyDown;

        static Func<KeyCode, bool> BindKeyMethod(Type input, string name)
        {
            MethodInfo method = input.GetMethod(name, new[] { typeof(KeyCode) });
            return method == null ? null : (Func<KeyCode, bool>)Delegate.CreateDelegate(typeof(Func<KeyCode, bool>), method);
        }

        static bool Prefix_AnyKey(ref bool __result)
        {
            if (!ShouldBlock())
                return true;
            __result = getKey != null && AnyJoystickButton(key => getKey((KeyCode)key));
            return false;
        }

        static bool Prefix_AnyKeyDown(ref bool __result)
        {
            if (!ShouldBlock())
                return true;
            __result = getKeyDown != null && AnyJoystickButton(key => getKeyDown((KeyCode)key));
            return false;
        }

        static bool Prefix_BlockVector2Out(ref Vector2 __0)
        {
            if (!ShouldBlock())
                return true;

            __0 = Vector2.zero;
            return false;
        }

#if CPP
        // ~~~~~~~~~~~~ Legacy Input (IL2CPP) ~~~~~~~~~~~~
        //
        // These Input methods are iCalls. When a patched iCall is detoured, letting Harmony run the "original" goes through
        // il2cpp_runtime_invoke on the same MethodInfo, whose method pointer is now our detour, so it recurses until the stack
        // overflows (e.g. BONELAB's BoidController calling Input.GetKeyDownInt). Instead, the prefixes below never run the
        // original: they return the blocked value, or call the native iCall directly (resolved via il2cpp_resolve_icall,
        // which isn't detoured). A method is only patched if its iCall could be resolved.

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        delegate bool d_IntToBool(int value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        delegate bool d_StringToBool(IntPtr il2cppString);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate float d_StringToFloat(IntPtr il2cppString);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void d_Vector2Out(out Vector2 value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        delegate bool d_Bool();

        static d_IntToBool icall_GetKeyInt, icall_GetKeyDownInt, icall_GetKeyUpInt;
        static d_IntToBool icall_GetMouseButton, icall_GetMouseButtonDown, icall_GetMouseButtonUp;
        static d_StringToBool icall_GetKeyString, icall_GetKeyDownString, icall_GetKeyUpString;
        static d_StringToBool icall_GetButton, icall_GetButtonDown, icall_GetButtonUp;
        static d_StringToFloat icall_GetAxis, icall_GetAxisRaw;
        static d_Bool icall_anyKey, icall_anyKeyDown;
        static d_Vector2Out icall_mouseScrollDelta;

        static void InitCppLegacyPatches(Type input)
        {
            Type[] keyCode = { typeof(KeyCode) };
            Type[] str = { typeof(string) };
            Type[] integer = { typeof(int) };

            int patched = 0;

            // Resolves the iCall into `icall`, then patches each target with `prefix` if it resolved.
            // String iCalls changed calling convention in Unity 6 (xxx_Injected with a span), so only exact signatures are used.
            void PatchICall<T>(ref T icall, string signature, string prefix, params (string name, Type[] args, MethodType type)[] targets) where T : Delegate
            {
                if (!ICallManager.TryGetICallExact(signature, out icall))
                    return;

                MethodInfo prefixMethod = AccessTools.Method(typeof(InputBlocker), prefix);
                foreach ((string name, Type[] args, MethodType type) in targets)
                {
                    if (Universe.Patch(input, name, type, args, prefix: prefixMethod))
                        patched++;
                }
            }

            const MethodType Normal = MethodType.Normal;
            PatchICall(ref icall_GetKeyInt, "UnityEngine.Input::GetKeyInt", nameof(Cpp_GetKeyInt), ("GetKey", keyCode, Normal), ("GetKeyInt", keyCode, Normal));
            PatchICall(ref icall_GetKeyDownInt, "UnityEngine.Input::GetKeyDownInt", nameof(Cpp_GetKeyDownInt), ("GetKeyDown", keyCode, Normal), ("GetKeyDownInt", keyCode, Normal));
            PatchICall(ref icall_GetKeyUpInt, "UnityEngine.Input::GetKeyUpInt", nameof(Cpp_GetKeyUpInt), ("GetKeyUp", keyCode, Normal), ("GetKeyUpInt", keyCode, Normal));
            PatchICall(ref icall_GetKeyString, "UnityEngine.Input::GetKeyString", nameof(Cpp_GetKeyString), ("GetKey", str, Normal), ("GetKeyString", str, Normal));
            PatchICall(ref icall_GetKeyDownString, "UnityEngine.Input::GetKeyDownString", nameof(Cpp_GetKeyDownString), ("GetKeyDown", str, Normal), ("GetKeyDownString", str, Normal));
            PatchICall(ref icall_GetKeyUpString, "UnityEngine.Input::GetKeyUpString", nameof(Cpp_GetKeyUpString), ("GetKeyUp", str, Normal), ("GetKeyUpString", str, Normal));
            PatchICall(ref icall_GetAxis, "UnityEngine.Input::GetAxis", nameof(Cpp_GetAxis), ("GetAxis", str, Normal));
            PatchICall(ref icall_GetAxisRaw, "UnityEngine.Input::GetAxisRaw", nameof(Cpp_GetAxisRaw), ("GetAxisRaw", str, Normal));
            PatchICall(ref icall_GetButton, "UnityEngine.Input::GetButton", nameof(Cpp_GetButton), ("GetButton", str, Normal));
            PatchICall(ref icall_GetButtonDown, "UnityEngine.Input::GetButtonDown", nameof(Cpp_GetButtonDown), ("GetButtonDown", str, Normal));
            PatchICall(ref icall_GetButtonUp, "UnityEngine.Input::GetButtonUp", nameof(Cpp_GetButtonUp), ("GetButtonUp", str, Normal));
            PatchICall(ref icall_anyKey, "UnityEngine.Input::get_anyKey", nameof(Cpp_anyKey), ("anyKey", null, MethodType.Getter));
            PatchICall(ref icall_anyKeyDown, "UnityEngine.Input::get_anyKeyDown", nameof(Cpp_anyKeyDown), ("anyKeyDown", null, MethodType.Getter));
            PatchICall(ref icall_GetMouseButton, "UnityEngine.Input::GetMouseButton", nameof(Cpp_GetMouseButton), ("GetMouseButton", integer, Normal));
            PatchICall(ref icall_GetMouseButtonDown, "UnityEngine.Input::GetMouseButtonDown", nameof(Cpp_GetMouseButtonDown), ("GetMouseButtonDown", integer, Normal));
            PatchICall(ref icall_GetMouseButtonUp, "UnityEngine.Input::GetMouseButtonUp", nameof(Cpp_GetMouseButtonUp), ("GetMouseButtonUp", integer, Normal));
            PatchICall(ref icall_mouseScrollDelta, "UnityEngine.Input::get_mouseScrollDelta_Injected", nameof(Cpp_mouseScrollDelta), ("get_mouseScrollDelta_Injected", new[] { typeof(Vector2).MakeByRefType() }, Normal));

            Universe.Log($"Input blocking: patched {patched} legacy Input methods (IL2CPP, calling iCalls directly)");
        }

        // `block` says whether this particular input can come from keyboard/mouse; ShouldBlock() is only evaluated if it can.
        static bool CppInt(d_IntToBool icall, bool block, int value, ref bool __result) { __result = !(block && ShouldBlock()) && icall(value); return false; }
        static bool CppString(d_StringToBool icall, bool block, string value, ref bool __result) { __result = !(block && ShouldBlock()) && icall(IL2CPP.ManagedStringToIl2Cpp(value)); return false; }
        static bool CppAxis(d_StringToFloat icall, string value, ref float __result) { __result = ShouldBlock() ? 0f : icall(IL2CPP.ManagedStringToIl2Cpp(value)); return false; }

        static bool CppAnyKey(d_Bool icall, d_IntToBool joystickKey, ref bool __result)
        {
            __result = ShouldBlock()
                ? AnyJoystickButton(joystickKey == null ? null : key => joystickKey(key))
                : icall();
            return false;
        }

        static bool Cpp_GetKeyInt(KeyCode __0, ref bool __result) => CppInt(icall_GetKeyInt, IsKeyboardOrMouse(__0), (int)__0, ref __result);
        static bool Cpp_GetKeyDownInt(KeyCode __0, ref bool __result) => CppInt(icall_GetKeyDownInt, IsKeyboardOrMouse(__0), (int)__0, ref __result);
        static bool Cpp_GetKeyUpInt(KeyCode __0, ref bool __result) => CppInt(icall_GetKeyUpInt, IsKeyboardOrMouse(__0), (int)__0, ref __result);
        static bool Cpp_GetMouseButton(int __0, ref bool __result) => CppInt(icall_GetMouseButton, true, __0, ref __result);
        static bool Cpp_GetMouseButtonDown(int __0, ref bool __result) => CppInt(icall_GetMouseButtonDown, true, __0, ref __result);
        static bool Cpp_GetMouseButtonUp(int __0, ref bool __result) => CppInt(icall_GetMouseButtonUp, true, __0, ref __result);
        static bool Cpp_GetKeyString(string __0, ref bool __result) => CppString(icall_GetKeyString, IsKeyboardOrMouse(__0), __0, ref __result);
        static bool Cpp_GetKeyDownString(string __0, ref bool __result) => CppString(icall_GetKeyDownString, IsKeyboardOrMouse(__0), __0, ref __result);
        static bool Cpp_GetKeyUpString(string __0, ref bool __result) => CppString(icall_GetKeyUpString, IsKeyboardOrMouse(__0), __0, ref __result);
        static bool Cpp_GetButton(string __0, ref bool __result) => CppString(icall_GetButton, true, __0, ref __result);
        static bool Cpp_GetButtonDown(string __0, ref bool __result) => CppString(icall_GetButtonDown, true, __0, ref __result);
        static bool Cpp_GetButtonUp(string __0, ref bool __result) => CppString(icall_GetButtonUp, true, __0, ref __result);
        static bool Cpp_GetAxis(string __0, ref float __result) => CppAxis(icall_GetAxis, __0, ref __result);
        static bool Cpp_GetAxisRaw(string __0, ref float __result) => CppAxis(icall_GetAxisRaw, __0, ref __result);
        static bool Cpp_anyKey(ref bool __result) => CppAnyKey(icall_anyKey, icall_GetKeyInt, ref __result);
        static bool Cpp_anyKeyDown(ref bool __result) => CppAnyKey(icall_anyKeyDown, icall_GetKeyDownInt, ref __result);

        static bool Cpp_mouseScrollDelta(ref Vector2 __0)
        {
            if (ShouldBlock())
                __0 = Vector2.zero;
            else
                icall_mouseScrollDelta(out __0);
            return false;
        }
#endif

        // ~~~~~~~~~~~~ InputSystem ~~~~~~~~~~~~

        static bool inputSystemChecked;
        static MethodInfo m_ListEnabledActions;
        static MethodInfo m_Enable;
        static MethodInfo m_Disable;
        static PropertyInfo p_actionMap;
        static PropertyInfo p_asset;
        static PropertyInfo p_controls;
        static PropertyInfo p_device;
        static PropertyInfo p_mapDevices;
        static PropertyInfo p_allDevices;
        static readonly List<object> frozenActions = new();
        // Action maps whose devices were restricted, with their previous `devices` value to restore.
        sealed class MaskedMap
        {
            public object map;
            public object previousDevices;
        }
        static readonly List<MaskedMap> maskedMaps = new();
        static bool deviceMaskingFailed;

        // Scanning inspects every enabled action's controls through reflection, so don't do it every frame.
        const int ScanIntervalFrames = 10;
        static int nextScanFrame;

        static bool InitInputSystemReflection()
        {
            if (inputSystemChecked)
                return m_ListEnabledActions != null;

            inputSystemChecked = true;
            try
            {
                Type tInputSystem = ReflectionUtility.GetTypeByName("UnityEngine.InputSystem.InputSystem");
                Type tAction = ReflectionUtility.GetTypeByName("UnityEngine.InputSystem.InputAction");
                Type tActionMap = ReflectionUtility.GetTypeByName("UnityEngine.InputSystem.InputActionMap");
                if (tInputSystem == null || tAction == null || tActionMap == null)
                    return false;

                m_Enable = tAction.GetMethod("Enable", ArgumentUtility.EmptyTypes);
                m_Disable = tAction.GetMethod("Disable", ArgumentUtility.EmptyTypes);
                p_actionMap = tAction.GetProperty("actionMap");
                p_asset = tActionMap.GetProperty("asset");
                p_controls = tAction.GetProperty("controls");
                p_device = ReflectionUtility.GetTypeByName("UnityEngine.InputSystem.InputControl")?.GetProperty("device");
                p_mapDevices = tActionMap.GetProperty("devices");
                p_allDevices = tInputSystem.GetProperty("devices", BindingFlags.Public | BindingFlags.Static);
                m_ListEnabledActions = tInputSystem.GetMethod("ListEnabledActions", BindingFlags.Public | BindingFlags.Static, null, ArgumentUtility.EmptyTypes, null);

                if (m_ListEnabledActions == null || m_Enable == null || m_Disable == null || p_controls == null || p_device == null
                    || InputSystem.TKeyboard == null || InputSystem.TMouse == null)
                {
                    m_ListEnabledActions = null;
                    return false;
                }

                Universe.Log("Input blocking: keyboard/mouse InputSystem bindings will be blocked while the UI is open");
                return true;
            }
            catch (Exception ex)
            {
                Universe.LogWarning($"Input blocking: failed to set up InputSystem support: {ex}");
                m_ListEnabledActions = null;
                return false;
            }
        }

        static void FreezeInputSystemActions()
        {
            if (!InitInputSystemReflection())
                return;

            // Scan right away when the UI opens, then every few frames to catch actions the game enables later.
            int frame = Time.frameCount;
            if ((frozenActions.Count > 0 || maskedMaps.Count > 0) && frame < nextScanFrame)
                return;
            nextScanFrame = frame + ScanIntervalFrames;

            try
            {
                object ourAsset = GetOwnActionsAsset();
                object list = m_ListEnabledActions.Invoke(null, null);
                if (list == null || !ReflectionUtility.TryGetEnumerator(list, out IEnumerator enumerator))
                    return;

                // Collect first, since disabling actions / changing devices modifies the enabled-actions state.
                List<object> toDisable = new();
                List<object> toMask = new();
                while (enumerator.MoveNext())
                {
                    object action = enumerator.Current;
                    if (action == null)
                        continue;

                    object map = p_actionMap.GetValue(action, null);
                    if (ourAsset != null)
                    {
                        object asset = map != null ? p_asset?.GetValue(map, null) : null;
                        if (asset != null && asset.ReferenceEqual(ourAsset))
                            continue;
                    }

                    GetBoundDeviceKinds(action, out bool keyboardOrMouse, out bool otherDevices);
                    if (!keyboardOrMouse)
                        continue; // can't be triggered by keyboard/mouse (e.g. XR-only), leave it alone

                    // Bound to keyboard/mouse and something else (e.g. an XR controller): restrict the map to the other
                    // devices so only the keyboard/mouse bindings stop working. Otherwise just disable the action.
                    if (otherDevices && map != null && !deviceMaskingFailed)
                    {
                        if (!toMask.Any(m => m.ReferenceEqual(map)) && !maskedMaps.Any(m => m.map.ReferenceEqual(map)))
                            toMask.Add(map);
                    }
                    else
                        toDisable.Add(action);
                }

                foreach (object map in toMask)
                {
                    if (!TryMaskKeyboardMouse(map))
                    {
                        // Restricting devices isn't possible here; fall back to disabling the actions entirely
                        // (on the next scan), since anything keyboard/mouse can trigger must be blocked.
                        nextScanFrame = 0;
                        break;
                    }
                }

                foreach (object action in toDisable)
                {
                    m_Disable.Invoke(action, null);
                    frozenActions.Add(action);
                }
            }
            catch (Exception ex)
            {
                Universe.LogWarning($"Input blocking: exception disabling InputSystem actions: {ex}");
                m_ListEnabledActions = null; // don't spam every frame
            }
        }

        static bool IsKeyboardOrMouseDevice(object device)
        {
            Type deviceType = device.GetActualType();
            return InputSystem.TKeyboard.IsAssignableFrom(deviceType) || InputSystem.TMouse.IsAssignableFrom(deviceType);
        }

        /// <summary>Which kinds of devices the controls the action is currently bound to belong to.</summary>
        static void GetBoundDeviceKinds(object action, out bool keyboardOrMouse, out bool otherDevices)
        {
            keyboardOrMouse = otherDevices = false;
            try
            {
                object controls = p_controls.GetValue(action, null);
                if (controls == null || !ReflectionUtility.TryGetEnumerator(controls, out IEnumerator enumerator))
                    return;

                while (enumerator.MoveNext())
                {
                    object device = enumerator.Current != null ? p_device.GetValue(enumerator.Current, null) : null;
                    if (device == null)
                        continue;

                    if (IsKeyboardOrMouseDevice(device))
                        keyboardOrMouse = true;
                    else
                        otherDevices = true;
                }
            }
            catch
            {
                // Can't tell; err on the side of leaving it alone.
                keyboardOrMouse = false;
            }
        }

        /// <summary>
        /// Restricts the action map to every connected device except keyboards and mice (InputActionMap.devices),
        /// so its keyboard/mouse bindings stop working while bindings to other devices keep working.
        /// </summary>
        static bool TryMaskKeyboardMouse(object map)
        {
            try
            {
                if (p_mapDevices == null || p_allDevices == null)
                    throw new MissingMemberException("InputActionMap.devices / InputSystem.devices");

                List<object> devices = new();
                object all = p_allDevices.GetValue(null, null);
                if (all != null && ReflectionUtility.TryGetEnumerator(all, out IEnumerator enumerator))
                {
                    while (enumerator.MoveNext())
                    {
                        if (enumerator.Current != null && !IsKeyboardOrMouseDevice(enumerator.Current))
                            devices.Add(enumerator.Current);
                    }
                }

                object previous = p_mapDevices.GetValue(map, null);
                p_mapDevices.SetValue(map, CreateDeviceList(p_mapDevices.PropertyType, devices), null);
                maskedMaps.Add(new MaskedMap { map = map, previousDevices = previous });
                return true;
            }
            catch (Exception ex)
            {
                deviceMaskingFailed = true;
                Universe.LogWarning("Input blocking: couldn't restrict InputSystem devices, actions bound to both keyboard/mouse and " +
                    $"other devices will be disabled entirely while the UI is open: {ex.ReflectionExToString()}");
                return false;
            }
        }

        /// <summary>
        /// Builds a value of <paramref name="type"/> holding <paramref name="devices"/>. Handles
        /// Nullable&lt;ReadOnlyArray&lt;InputDevice&gt;&gt; on Mono (ReadOnlyArray has a T[] constructor), and the Il2CppInterop
        /// equivalents on IL2CPP (Il2CppSystem.Nullable, ReadOnlyArray taking an Il2CppReferenceArray).
        /// </summary>
        static object CreateDeviceList(Type type, List<object> devices)
        {
            // A boxed T can be assigned to a System.Nullable<T> property directly.
            type = Nullable.GetUnderlyingType(type) ?? type;

            // Skip Il2CppInterop's ctor(IntPtr pointer), which wraps an existing native object.
            ConstructorInfo ctor = type.GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType != typeof(IntPtr))
                ?? throw new MissingMethodException(type.FullName, ".ctor");
            Type argType = ctor.GetParameters()[0].ParameterType;

            object arg;
            if (argType.IsArray)
                arg = ToTypedArray(argType.GetElementType(), devices);
            else if (argType.IsGenericType && argType.Name.StartsWith("Il2CppReferenceArray"))
                arg = Activator.CreateInstance(argType, ToTypedArray(argType.GetGenericArguments()[0], devices));
            else
                arg = CreateDeviceList(argType, devices); // e.g. Il2CppSystem.Nullable<ReadOnlyArray<InputDevice>>

            return ctor.Invoke(new[] { arg });
        }

        static Array ToTypedArray(Type elementType, List<object> items)
        {
            Array array = Array.CreateInstance(elementType, items.Count);
            for (int i = 0; i < items.Count; i++)
                array.SetValue(items[i].TryCast(elementType), i);
            return array;
        }

        static void RestoreInputSystemActions()
        {
            nextScanFrame = 0;

            for (int i = maskedMaps.Count - 1; i >= 0; i--)
            {
                try
                {
                    p_mapDevices.SetValue(maskedMaps[i].map, maskedMaps[i].previousDevices, null);
                }
                catch (Exception ex)
                {
                    Universe.LogWarning($"Input blocking: exception restoring InputSystem devices: {ex.ReflectionExToString()}");
                }
            }
            maskedMaps.Clear();

            if (frozenActions.Count == 0)
                return;

            foreach (object action in frozenActions)
            {
                try
                {
                    m_Enable.Invoke(action, null);
                }
                catch (Exception ex)
                {
                    Universe.LogWarning($"Input blocking: exception re-enabling InputSystem action: {ex.ReflectionExToString()}");
                }
            }
            frozenActions.Clear();
        }

        /// <summary>The actions asset used by UniverseLib's own InputSystemUIInputModule, if any, so we don't disable our own UI input.</summary>
        static object GetOwnActionsAsset()
        {
            try
            {
                BaseInputModule module = EventSystemHelper.UIInput;
                if (!module)
                    return null;

                Type tModule = ReflectionUtility.GetTypeByName("UnityEngine.InputSystem.UI.InputSystemUIInputModule");
                if (tModule == null)
                    return null;

                object casted = module.TryCast(tModule);
                return tModule.GetProperty("actionsAsset")?.GetValue(casted, null);
            }
            catch
            {
                return null;
            }
        }
    }
}
