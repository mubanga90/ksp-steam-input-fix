using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

[assembly: KSPAssembly("SteamInputFix", 1, 0)]

namespace SteamInputFix
{
	[KSPAddon(KSPAddon.Startup.Instantly, true)]
	public class SteamInputFixLoader : MonoBehaviour
	{
		private const string TAG = "[SteamInputFix]";
		private static bool patched = false;

		public static Type kspControllerModesType;
		public static object modeMenu, modeFlight, modeMap, modeEva;

		void Awake()
		{
			if (patched) { Destroy(gameObject); return; }
			DontDestroyOnLoad(gameObject);

			try
			{
				Type kspSteamControllerType = null;
				foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					Type[] types;
					try { types = asm.GetTypes(); }
					catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
					catch { continue; }

					kspSteamControllerType = types.FirstOrDefault(t =>
						t != null && t.FullName == "SteamController.KSPSteamController");
					if (kspSteamControllerType != null) break;
				}

				if (kspSteamControllerType == null)
				{
					Debug.LogWarning($"{TAG} KSPSteamController type not found — Steam controller plugin not present? Skipping.");
					return;
				}

				kspControllerModesType = kspSteamControllerType.GetNestedType("KSPControllerModes",
					BindingFlags.Public | BindingFlags.NonPublic);
				if (kspControllerModesType == null)
				{
					Debug.LogError($"{TAG} KSPControllerModes nested enum not found");
					return;
				}

				try { modeMenu = Enum.Parse(kspControllerModesType, "Menu"); } catch { }
				try { modeFlight = Enum.Parse(kspControllerModesType, "Flight"); } catch { }
				try { modeMap = Enum.Parse(kspControllerModesType, "Map"); } catch { }
				// EVA is optional: the handler self-disables if the value is absent.
				try { modeEva = Enum.Parse(kspControllerModesType, "EVA"); } catch { }

				if (modeMenu == null || modeFlight == null || modeMap == null)
				{
					Debug.LogError($"{TAG} Could not parse all required enum values");
					return;
				}

				var method = kspSteamControllerType.GetMethod("GetModeForCurrentContext",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (method == null)
				{
					Debug.LogError($"{TAG} GetModeForCurrentContext not found");
					return;
				}

				var harmony = new Harmony("SteamInputFix");
				harmony.Patch(method, null,
					new HarmonyMethod(typeof(Patches), nameof(Patches.GetModeForCurrentContext_Postfix)));

				patched = true;
				Debug.Log($"{TAG} v1.3.0 installed. Flight-control fixes: Map view always uses Map controls (incl. from Docking); EVA uses EVA controls; the maneuver burn-info panel uses Flight/Map controls (was Menu), while editing a node keeps the game's Menu controls.");
			}
			catch (Exception ex)
			{
				Debug.LogError($"{TAG} Install failed: {ex}");
			}
		}
	}

	public static class Patches
	{
		// KSPSteamController.GetModeForCurrentContext (in GameData/Squad/Plugins/
		// KSPSteamCtrlr.dll) decides the controller action set, but only handles a
		// subset of flight states correctly:
		//   - STAGING/DOCKING: check isEVA but IGNORE MapView.MapIsEnabled, so the map
		//     view keeps Flight/Docking controls instead of Map.
		//   - MANEUVER_EDIT/MANEUVER_INFO: not in its switch at all, so both fall through
		//     to Menu. MANEUVER_EDIT (dragging the gizmo) genuinely wants Menu/pointer
		//     controls, but MANEUVER_INFO (just the burn-info panel) should use normal
		//     flight controls.
		// It's called event-driven (FlightUIMode change, vessel change, maneuver toggle,
		// unpause) via SetControllerMode(GetModeForCurrentContext()), so fixing the
		// return value here propagates. Only the flight scene is touched; the
		// editor/tracking-station/menu branches are already correct.
		public static void GetModeForCurrentContext_Postfix(ref object __result)
		{
			try
			{
				if (!HighLogic.LoadedSceneIsFlight) return;       // editor/trackstation/menu: leave as-is
				if (FlightUIModeController.Instance == null) return;
				if (FlightDriver.Pause) return;                   // paused: game returns Menu, correct

				var uiMode = FlightUIModeController.Instance.Mode;

				// Editing/placing a maneuver node needs the game's Menu (pointer)
				// controls to grab the gizmo — leave it exactly as the game set it.
				if (uiMode == FlightUIMode.MANEUVER_EDIT) return;

				// Map view always uses Map controls (the game only honours this in its
				// MAPMODE case, so Stage/Dock/maneuver-info keep the wrong set in map).
				if (MapView.MapIsEnabled)
				{
					__result = SteamInputFixLoader.modeMap;
				}
				// Controlling an EVA Kerbal: EVA controls (issue #4).
				else if (IsActiveVesselEva() && SteamInputFixLoader.modeEva != null)
				{
					__result = SteamInputFixLoader.modeEva;
				}
				// Burn-info panel (node present, not being edited): the game returns
				// Menu — use normal flight controls.
				else if (uiMode == FlightUIMode.MANEUVER_INFO)
				{
					__result = SteamInputFixLoader.modeFlight;
				}
				// STAGING/DOCKING/MAPMODE without map: the game already returns the
				// correct mode (Flight/Docking/Map) — leave __result untouched.
			}
			catch { /* never let our patch break the game */ }
		}

		private static bool IsActiveVesselEva()
		{
			var v = FlightGlobals.ActiveVessel;
			return v != null && v.isEVA;
		}
	}
}