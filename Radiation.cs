using System;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;

using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

using Barotrauma.Abilities;
using Barotrauma.Extensions;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Xml.Linq;

namespace JovianRadiationRework
{
  public partial class Mod : IAssemblyPlugin
  {

    [HarmonyPatch(typeof(Radiation))]
    public class RadiationPatch
    {
      public static double lastSetMetadataTiming = 0;
      public static void SetCampaignMetadata()
      {
        if (Timing.TotalTime - lastSetMetadataTiming > 1.0)
        {
          lastSetMetadataTiming = Timing.TotalTime;
          SetMetadata("CurrentLocationIrradiation", CurrentLocationRadiationAmount());
          SetMetadata("MainSubIrradiation", EntityRadiationAmount(Submarine.MainSub));
        }
      }



      [HarmonyPrefix]
      [HarmonyPatch("OnStep")]
      public static bool Radiation_OnStep_Replace(Radiation __instance, float steps = 1)
      {
        if (settings.Mod.UseVanillaRadiation)
        {
          SetMetadata("CurrentLocationIrradiation", CurrentLocationRadiationAmount());
          return true;
        }

        Radiation _ = __instance;

        if (!_.Enabled)
        {
          SetMetadata("CurrentLocationIrradiation", CurrentLocationRadiationAmount());
          return false;
        }
        if (steps <= 0)
        {
          SetMetadata("CurrentLocationIrradiation", CurrentLocationRadiationAmount());
          return false;
        }

        float percentageCovered = _.Amount / _.Map.Width;
        float speedMult = Math.Clamp(1 - (1 - settings.Mod.Progress.TargetSpeedPercentageAtTheEndOfTheMap) * percentageCovered, 0, 1);

        Info($"map.width {_.Map.Width} Amount {_.Amount} speedMult {speedMult}");

        float increaseAmount = Math.Max(0, _.Params.RadiationStep * speedMult * steps);

        if (_.Params.MaxRadiation > 0 && _.Params.MaxRadiation < _.Amount + increaseAmount)
        {
          increaseAmount = _.Params.MaxRadiation - _.Amount;
        }

        Info($"Radiation.Amount += {increaseAmount}");

        _.IncreaseRadiation(increaseAmount);

        int amountOfOutposts = _.Map.Locations.Count(location => location.Type.HasOutpost && !location.IsCriticallyRadiated());

        foreach (Location location in _.Map.Locations.Where(l => _.DepthInRadiation(l) > 0))
        {
          if (location.IsGateBetweenBiomes)
          {
            location.Connections.ForEach(c => c.Locked = false);
            //continue;
          }

          if (amountOfOutposts <= _.Params.MinimumOutpostAmount) { break; }

          if (settings.Mod.Progress.KeepSurroundingOutpostsAlive && _.Map.CurrentLocation is { } currLocation)
          {
            // Don't advance on nearby locations to avoid buggy behavior
            if (currLocation == location || currLocation.Connections.Any(lc => lc.OtherLocation(currLocation) == location)) { continue; }
          }

          bool wasCritical = location.IsCriticallyRadiated();

          location.TurnsInRadiation++;

          if (location.Type.HasOutpost && !wasCritical && location.IsCriticallyRadiated())
          {
            location.ClearMissions();
            amountOfOutposts--;
          }
        }

        SetMetadata("CurrentLocationIrradiation", CurrentLocationRadiationAmount());
        return false;
      }

      [HarmonyPrefix]
      [HarmonyPatch("UpdateRadiation")]
      public static bool Radiation_UpdateRadiation_Replace(Radiation __instance, float deltaTime)
      {
        Stopwatch sw = new Stopwatch();

        if (settings.Mod.UseVanillaRadiation) return true;
        Radiation _ = __instance;

        //SetCampaignMetadata();

        if (!(GameMain.GameSession?.IsCurrentLocationRadiated() ?? false)) { return false; }

        if (GameMain.NetworkMember is { IsClient: true }) { return false; }

        if (_.radiationTimer > 0)
        {
          _.radiationTimer -= deltaTime;
          return false;
        }

        sw.Restart();

        Mod.Instance.electronicsDamager.DamageItems();

        _.radiationTimer = _.Params.RadiationDamageDelay;

        foreach (Character character in Character.CharacterList)
        {
          if (!character.IsOnPlayerTeam || character.IsDead || character.Removed || !(character.CharacterHealth is { } health)) { continue; }

          float radiationAmount = Math.Max(0, EntityRadiationAmount(character)) * settings.Mod.RadiationDamage;


          // Reduce damage in sub
          if (character.CurrentHull != null)
          {
            float gapSize = 0;
            foreach (Gap g in character.CurrentHull.ConnectedGaps)
            {
              if (g.linkedTo.Count == 1) gapSize += g.Open;
            }

            gapSize = Math.Clamp(gapSize, 0, 1);

            float mult = Math.Clamp(1 - (1 - gapSize) * settings.Mod.FractionOfRadiationBlockedInSub, 0, 1);

            radiationAmount *= mult;
            //Info($"{character}{character?.Info.Name} gap mult {mult}");
          }

          if (character.IsHuskInfected)
          {
            radiationAmount = Math.Max(0, radiationAmount - settings.Mod.HuskRadiationResistance * settings.Vanilla.RadiationDamageDelay);
          }

          if (radiationAmount > 0)
          {
            var limb = character.AnimController.MainLimb;
            AttackResult attackResult = limb.AddDamage(
              limb.SimPosition,
              AfflictionPrefab.JovianRadiation.Instantiate(radiationAmount).ToEnumerable(),
              playSound: false
            );

            // CharacterHealth.ApplyAffliction is simpler but it ignores gear
            character.CharacterHealth.ApplyDamage(limb, attackResult);
          }
        }

        sw.Stop();
        Info($"Rad update took: {sw.ElapsedTicks * TicksToMs}ms");

        return false;
      }
    }


  }
}