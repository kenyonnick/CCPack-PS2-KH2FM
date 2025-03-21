using ConnectorLib;
using CrowdControl.Games.SmartEffects;
using System;
using System.Collections.Generic;

namespace CrowdControl.Games.Packs.KH2FM;

public partial class KH2FM {
    [EffectHandler("kill_sora")]
    public class KillSora : BaseEffect
    {
        public KillSora(KH2FM pack) : base(pack) { }

        public override EffectHandlerType Type => EffectHandlerType.Instant;

        public override IList<String> Codes { get; } = new [] { EffectIds.KillSora };

        public override IList<String> Mutexes { get; } = new [] { EffectIds.KillSora };

        public override bool StartAction()
        {
            // TODO: Implement this. 
            // Returns false in case it makes it into the 
            // menu to protect from accidentally using coins
            return false;
        }
    }
}