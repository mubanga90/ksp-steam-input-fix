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
		public static object modeMenu, modeFlight, modeMap;

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
				Debug.Log($"{TAG} v1.0 installed. Fixes: maneuver-node panel keeps Flight/Map controls (was Menu); Map view uses Map controls (was Flight).");
			}
			catch (Exception ex)
			{
				Debug.LogError($"{TAG} Install failed: {ex}");
			}
		}
	}

	public static class Patches
	{
		public static void GetModeForCurrentContext_Postfix(ref object __result)
		{
			try
			{
				if (FlightUIModeController.Instance == null) return;
				HandleManeuverNodeBug(ref __result);
				HandleMapModeBug(ref __result);
			}
			catch { /* never let our patch break the game */ }
		}


		// Bug: KSPSteamController.GetModeForCurrentContext returns Menu when
		// FlightUIMode is MANEUVER_INFO. MANEUVER_INFO is just the burn-info
		// panel — no gizmo is being dragged — so Flight (or Map) controls are
		// appropriate. Without this fix, after closing a maneuver gizmo the
		// controller is stuck on menu controls until you leave/re-enter flight.
		private static void HandleManeuverNodeBug(ref object __result)
		{
			if (FlightUIModeController.Instance.Mode != FlightUIMode.MANEUVER_INFO) return;
			if (!__result.Equals(SteamInputFixLoader.modeMenu)) return;

			__result = MapView.MapIsEnabled
					? SteamInputFixLoader.modeMap
					: SteamInputFixLoader.modeFlight;
		}


		// Bug: For some reason, when entering Map view in flight, the Map controls
		// are immediately changed to Flight controls. This fix adds a re-check
		// to set controller mode to Map when map view is on screen.
		private static void HandleMapModeBug(ref object __result)
		{
			if (FlightUIModeController.Instance.Mode != FlightUIMode.STAGING) return;
			if (!__result.Equals(SteamInputFixLoader.modeFlight)) return;

			__result = MapView.MapIsEnabled
					? SteamInputFixLoader.modeMap
					: SteamInputFixLoader.modeFlight;
		}
	}
}