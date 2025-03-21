using ConnectorLib;
using CrowdControl.Common;
using CrowdControl.Games.SmartEffects;
using System;
using System.Collections.Generic;

namespace CrowdControl.Games.Packs.KH2FM;

public partial class KH2FM {
    [EffectHandler("defenseless_sora", "full_defense_sora")]
    public class DefenselessAndFulLDefense : BaseEffect
    {
        public DefenselessAndFulLDefense(KH2FM pack) : base(pack) { }

        public override EffectHandlerType Type => EffectHandlerType.Durational;

        public override IList<String> Codes { get; } = new [] { EffectIds.Defenseless };

        public override IList<String> Mutexes { get; } = new [] { EffectIds.Defenseless, EffectIds.ExpertMagician, EffectIds.AmnesiacMagician };

        private ushort? guardAbilityAddress;
        private byte guardAbility;
        private ushort? dodgeAbilityAddress;
        private byte dodgeAbility;
        private byte magnet;

        public override bool StartAction()
        {
            bool success = true;

            int guardValue = Lookup(AbilityValues.GuardToggleValue - AbilityValues.OffToggleAbilityValue, Ability.Values.GuardToggleValue);

            // Get guard Ability for Sora
            guardAbilityAddress = Utils.FindAbilityMemory(Connector, AbilityAddresses.SoraAbilitySlots, AbilityValues.SoraAbilityCount, AbilityValues.Guard);
            if (guardAbilityAddress != null)
            {
                // Add offset to get if Ability is On or Off
                success &= Connector.Read8(guardAbilityAddress + 1, out byte guardAbility);
                success &= Connector.Write8(guardAbilityAddress + 1, (byte)guardValue);
            }

            // I FORGOT IN THIS VERSION THERE ARE QUITE A LOT OF THEM SINCE THEY ARE A GROWTH ABILITY - DISABLING FOR NOW
            // Get Dodge Ability for Sora
            //dodgeAbilityAddress = Utils.FindAbilityMemory();
            //if (dodgeAbilityAddress != null)
            //{
            //    // Add offset to get if Ability is On or Off
            //    success &= Connector.Read8(dodgeAbilityAddress + 1, out uint dodgeAbility);
            //    success &= Connector.Write8(dodgeAbilityAddress + 1, 0);
            //}

            // Get Reflect Magic for Sora
            success &= Connector.Read8((ulong)MagicAddresses.Magnet, out magnet);
            success &= Connector.Write8((ulong)MagicAddresses.Magnet, (byte)MiscValues.None);

            return success;
        }

        public override bool StopAction() 
        {
            bool success = true;
            
            if (guardAbilityAddress != null)
            {
                success &= Connector.Write8(guardAbilityAddress + 1, guardAbility);
            }

            //if (dodgeAbilityAddress != null)
            //{
            //    success &= Connector.Write8(dodgeAbilityAddress + 1, dodgeAbility);
            //}

            success &= Connector.Write8((ulong)MagicAddresses.Magnet, (byte)MiscValues.None);

            return success;
        }
    }
}