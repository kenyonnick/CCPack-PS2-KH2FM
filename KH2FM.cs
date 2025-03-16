using ConnectorLib;
using ConnectorType = CrowdControl.Common.ConnectorType;
using CrowdControl.Common;
using JetBrains.Annotations;
using Log = CrowdControl.Common.Log;
using System.Diagnostics.CodeAnalysis;
using Timer = System.Timers.Timer;
using Newtonsoft.Json;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
// ReSharper disable CommentTypo

//ccpragma { "include" : [ ".\\Effects\\*.cs", ".\\Common\\**\*.cs" ] }
namespace CrowdControl.Games.Packs.KH2FM;

[UsedImplicitly]
public class KH2FM : PS2EffectPack
{
    public override Game Game { get; } = new(name: "Kingdom Hearts II: Final Mix", id: "KH2FM", path: "PS2", ConnectorType.PS2Connector);

    private readonly KH2FMCrowdControl kh2FMCrowdControl;

    public override EffectList Effects { get; }

    public KH2FM(UserRecord player, Func<CrowdControlBlock, bool> responseHandler, Action<object> statusUpdateHandler) : base(player, responseHandler, statusUpdateHandler)
    {
        Log.FileOutput = true;

        kh2FMCrowdControl = new KH2FMCrowdControl();
        Effects = kh2FMCrowdControl.Options.Select(x => new Effect(x.Value.Name, x.Value.Id)
        {
            Price = (uint)x.Value.Cost,
            Description = x.Value.Description,
            Duration = SITimeSpan.FromSeconds(x.Value.DurationSeconds),
            Category = x.Value.GetEffectCategory(),
            Group = x.Value.GetEffectGroup(),
        }).ToList();

        Timer timer = new(1000.0);
        timer.Elapsed += (_, _) =>
        {
            if (kh2FMCrowdControl.CheckTPose(Connector))
            {
                kh2FMCrowdControl.FixTPose(Connector);
            }
        };

        timer.Start();
    }

    private bool GetOptionForRequest(EffectRequest request, [MaybeNullWhen(false)] out Option option)
    {
        string effectId = FinalCode(request);
        Log.Debug($"Requested Effect Id (FinalCode): {effectId}");
        var availableEffectIds = kh2FMCrowdControl.Options.Select((pair) => pair.Key).ToList();
        Log.Debug("Available Effect Ids: " + string.Join(", ", availableEffectIds));
        bool effectIsAvailable = kh2FMCrowdControl.Options.Any(x => x.Key == effectId);
        Log.Debug($"Is Requested Effect Id Available: {effectIsAvailable}"); 
        return kh2FMCrowdControl.Options.TryGetValue(effectId, out option);
    }

    private string[] GetOptionConflictsForRequest(string optionId)
    {
        if(kh2FMCrowdControl.OptionConflicts.TryGetValue(optionId, out string[]? conflicts)) {
            return conflicts;
        } else {
            return new string[0];
        }
    }

    #region Game State Checks

    private bool IsGameInPlay() => IsReady(null);

    // do all actual statechecking here - kat
    protected override GameState GetGameState()
    {
        bool success = true;
        string gameStateString = string.Empty;

        success &= Connector.Read32LE(0x2035F314, out uint gameState);
        success &= Connector.Read32LE(0x20341708, out uint animationStateOffset);
        success &= Connector.Read32LE(0x2033CC38, out uint cameraLockState);
        success &= Connector.Read32LE(0x21C60CE0, out uint transitionState);

        if (!success)
        {
            return GameState.Unknown;
        }

        // Set the state

        if (gameState == 1 && cameraLockState == 0 && transitionState == 1 && animationStateOffset != 0)
        {
            gameStateString = "Ready";
        }
        else if (gameState == 1 && cameraLockState == 1 && transitionState == 1 && animationStateOffset != 0)
        {
            gameStateString = "Uncontrollable";
        }
        else if (gameState == 0 && cameraLockState == 0 && transitionState == 0 && animationStateOffset == 0)
        {
            gameStateString = "Dead";
        }
        else if (gameState == 0 && cameraLockState == 0 && transitionState == 1 && animationStateOffset != 0)
        {
            gameStateString = "Pause";
        }
        else if (gameState == 1 && cameraLockState == 1 && transitionState == 0 && animationStateOffset == 0)
        {
            gameStateString = "Cutscene";
        }
        else
        {
            gameStateString = "Unknown";
        }

        if (gameStateString != "Ready")
        {
            return GameState.WrongMode;
        }
        // it would be awesome if someone could fill this in a bit more - kat

        return GameState.Ready;
    }

    #endregion

    protected override void StartEffect(EffectRequest request)
    {
        if (!GetOptionForRequest(request, out Option? option))
        {
            Respond(request, EffectStatus.FailPermanent, StandardErrors.UnknownEffect, request);
            return;
        }

        string[] conflicts = GetOptionConflictsForRequest(option.Id);

        switch (option.effectFunction)
        {
            case EffectFunction.StartTimed:
                var timed = StartTimed(
                    request: request,
                    startCondition: () => IsGameInPlay(),
                    continueCondition: () => IsGameInPlay(),
                    continueConditionInterval: TimeSpan.FromMilliseconds(500),
                    action: () => option.StartEffect(Connector),
                    mutex: conflicts
                );
                timed.WhenCompleted.Then(_ => option.StopEffect(Connector));
                break;
            case EffectFunction.RepeatAction:
                var action = RepeatAction(
                    request: request,
                    startCondition: () => IsGameInPlay(),
                    startAction: () => option.StartEffect(Connector),
                    startRetry: TimeSpan.FromSeconds(1),
                    refreshCondition: () => IsGameInPlay(),
                    refreshRetry: TimeSpan.FromMilliseconds(500),
                    refreshAction: () => option.DoEffect(Connector),
                    refreshInterval: TimeSpan.FromMilliseconds(option.RefreshInterval),
                    extendOnFail: true,
                    mutex: conflicts
                );
                action.WhenCompleted.Then(_ => option.StopEffect(Connector));
                break;
            default:
                TryEffect(
                    request: request,
                    condition: () => IsGameInPlay(),
                    action: () => option.StartEffect(Connector),
                    followUp: () => option.StopEffect(Connector),
                    retryDelay: TimeSpan.FromMilliseconds(500),
                    retryOnFail: true,
                    mutex: conflicts,
                    holdMutex: TimeSpan.FromMilliseconds(500)
                );
                break;

        }
    }

    protected override bool StopEffect(EffectRequest request)
    {
        Log.Message($"[StopEffect] request.EffectId = {request.EffectID}");

        if (GetOptionForRequest(request, out Option? option)) return option.StopEffect(Connector);
        return base.StopEffect(request);
    }

    public override bool StopAllEffects()
    {
        bool success = base.StopAllEffects();
        try
        {
            foreach (Option o in kh2FMCrowdControl.Options.Values)
            {
                success &= o.StopEffect(Connector);
            }
        }
        catch
        {
            success = false;
        }
        return success;
    }
}



public abstract class Option
{
    public static string ToId(Category category, SubCategory subCategory, string objectName)
    {
        string modifiedCategory = category.ToString().Replace(" ", "_").Replace("'", "").ToLower();
        string modifiedSubCategory = subCategory.ToString().Replace(" ", "_").Replace("'", "").ToLower();
        string modifiedObjectName = objectName.Replace(" ", "_").Replace("'", "").ToLower();

        return $"{modifiedCategory}_{modifiedSubCategory}_{modifiedObjectName}";
    }

    private bool isActive = false;
    protected Category category = Category.None;
    protected SubCategory subCategory = SubCategory.None;

    public EffectFunction effectFunction;

    public string Name { get; set; }
    public string Id
    {
        get
        {
            return ToId(category, subCategory, Name);
        }
    }

    public string Description { get; set; }
    public int Cost { get; set; }

    public int DurationSeconds { get; set; }
    public int RefreshInterval { get; set; } // In milliseconds

    public Option(string name, string description, Category category, SubCategory subCategory, EffectFunction effectFunction, int cost = 50, int durationSeconds = 0, int refreshInterval = 500)
    {
        Name = name;
        this.category = category;
        this.subCategory = subCategory;
        this.effectFunction = effectFunction;
        Cost = cost;
        Description = description;
        DurationSeconds = durationSeconds;
        RefreshInterval = refreshInterval;
    }

    public EffectGrouping? GetEffectCategory()
    {
        return category == Category.None ? null : new EffectGrouping(category.ToString());
    }

    public EffectGrouping? GetEffectGroup()
    {
        return subCategory == SubCategory.None ? null : new EffectGrouping(subCategory.ToString());
    }

    public abstract bool StartEffect(IPS2Connector connector);
    public virtual bool DoEffect(IPS2Connector connector) => true;
    public virtual bool StopEffect(IPS2Connector connector) => true;
}

public class KH2FMCrowdControl
{
    public Dictionary<string, Option> Options;
    public Dictionary<string, string[]> OptionConflicts;

    public KH2FMCrowdControl()
    {
        OneShotSora oneShotSora = new OneShotSora();
        HealSora healSora = new HealSora();
        Invulnerability invulnerability = new Invulnerability();
        MoneybagsSora moneybagsSora = new MoneybagsSora();
        RobSora robSora = new RobSora();
        GrowthSpurt growthSpurt = new GrowthSpurt();
        SlowgaSora slowgaSora = new SlowgaSora();
        TinyWeapon tinyWeapon = new TinyWeapon();
        GiantWeapon giantWeapon = new GiantWeapon();
        Struggling struggling = new Struggling();
        WhoAreThey whoAreThey = new WhoAreThey();
        HostileParty hostileParty = new HostileParty();
        ShuffleShortcuts shuffleShortcuts = new ShuffleShortcuts();
        HastegaSora hastegaSora = new HastegaSora();
        IAmDarkness iAmDarkness = new IAmDarkness();
        BackseatDriver backseatDriver = new BackseatDriver();
        ExpertMagician expertMagician = new ExpertMagician();
        AmnesiacMagician amnesiacMagician = new AmnesiacMagician();
        Itemaholic itemaholic = new Itemaholic();
        SpringCleaning springCleaning = new SpringCleaning();
        SummonChauffeur summonChauffeur = new SummonChauffeur();
        SummonTrainer summonTrainer = new SummonTrainer();
        HeroSora heroSora = new HeroSora();
        ZeroSora zeroSora = new ZeroSora();
        ProCodes proCodes = new ProCodes();
        EZCodes ezCodes = new EZCodes();

        Options = new List<Option>
            {
                oneShotSora,
                healSora,
                invulnerability,
                moneybagsSora,
                robSora,
                growthSpurt,
                tinyWeapon,
                giantWeapon,
                struggling,
                shuffleShortcuts,
                hastegaSora,
                iAmDarkness,
                backseatDriver,
                expertMagician,
                amnesiacMagician,
                itemaholic,
                springCleaning,
                summonChauffeur,
                summonTrainer,
                heroSora,
                zeroSora
            }.ToDictionary(x => x.Id, x => x);

        // Used to populate mutexes
        OptionConflicts = new Dictionary<string, string[]>
            {
                { oneShotSora.Id, [oneShotSora.Id, healSora.Id, invulnerability.Id] },
                { healSora.Id, [healSora.Id, oneShotSora.Id, invulnerability.Id] },
                { tinyWeapon.Id, [tinyWeapon.Id, giantWeapon.Id] },
                { giantWeapon.Id, [tinyWeapon.Id, giantWeapon.Id] },
                { iAmDarkness.Id, [iAmDarkness.Id, backseatDriver.Id, heroSora.Id, zeroSora.Id] },
                { backseatDriver.Id, [backseatDriver.Id, iAmDarkness.Id, heroSora.Id, zeroSora.Id] },
                { expertMagician.Id, [expertMagician.Id, amnesiacMagician.Id, heroSora.Id, zeroSora.Id] },
                { amnesiacMagician.Id, [amnesiacMagician.Id, expertMagician.Id, heroSora.Id, zeroSora.Id] },
                { itemaholic.Id, [itemaholic.Id, springCleaning.Id, heroSora.Id, zeroSora.Id] },
                { springCleaning.Id, [springCleaning.Id, itemaholic.Id, heroSora.Id, zeroSora.Id] },
                { summonChauffeur.Id, [summonChauffeur.Id, summonTrainer.Id, heroSora.Id, zeroSora.Id] },
                { summonTrainer.Id, [summonTrainer.Id, summonChauffeur.Id, heroSora.Id, zeroSora.Id] },
                { heroSora.Id, [heroSora.Id, zeroSora.Id, itemaholic.Id, springCleaning.Id, summonChauffeur.Id, summonTrainer.Id, expertMagician.Id, amnesiacMagician.Id
                    ]
                },
            };
    }

    public bool CheckTPose(IPS2Connector connector)
    {
        if (connector == null) return false;

        connector.Read64LE(0x20341708, out ulong animationStateOffset);
        connector.Read32LE(animationStateOffset + 0x2000014C, out uint animationState);

        return animationState == 0;
    }

    public void FixTPose(IPS2Connector connector)
    {
        if (connector == null) return;

        connector.Read64LE(0x20341708, out ulong animationStateOffset);

        connector.Read8(0x2033CC38, out byte cameraLock);

        if (cameraLock == 0)
        {
            connector.Read16LE(animationStateOffset + 0x2000000C, out ushort animationState);

            // 0x8001 is Idle state
            if (animationState != 0x8001)
            {
                connector.Write16LE(animationStateOffset + 0x2000000C, 0x40);
            }
        }
    }

    public static void TriggerReaction(IPS2Connector connector) {
        Timer timer = new()
        {
            AutoReset = true,
            Enabled = true,
            Interval = 10
        };

        timer.Elapsed += (obj, ev) =>
        {
            connector.Read16LE(DriveAddresses.ReactionEnable, out ushort value);

            if (value == 5 || DateTime.Compare(DateTime.Now, ev.SignalTime.AddSeconds(30)) > 0) timer.Stop();

            connector.Write8((ulong)DriveAddresses.ButtonPress, (byte)ButtonValues.Triangle);
            connector.Write8(0x2034D3C1, 0x10);
            connector.Write8(0x2034D4DD, 0xEF);
            connector.Write8(0x2034D466, 0xFF);
            connector.Write8(0x2034D4E6, 0xFF);
        };
        timer.Start();
    }

    #region Option Implementations
    private class OneShotSora : Option
    {
        public OneShotSora() : base("1 Shot Sora", "Sora only has 1 HP until the effect is over",
            Category.Sora, SubCategory.Stats,
            EffectFunction.RepeatAction, durationSeconds: 60)
        { }


        public override bool StartEffect(IPS2Connector connector)
        {
            return connector.Write32LE(StatAddresses.HP, 1);
        }

        public override bool DoEffect(IPS2Connector connector)
        {
            return connector.Write32LE(StatAddresses.HP, 1);
        }


        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;
            success &= connector.Read32LE(StatAddresses.MaxHP, out uint maxHP);
            success &= connector.Write32LE(StatAddresses.HP, maxHP);
            return success;
        }
    }

    private class HealSora : Option
    {
        public HealSora() : base("Heal Sora", "Heal Sora to Max HP.", Category.Sora, SubCategory.Stats, EffectFunction.TryEffect) { }

        public override bool StartEffect(IPS2Connector connector)
        {
            return connector.Read32LE(StatAddresses.MaxHP, out uint maxHP)
                && connector.Write32LE(StatAddresses.HP, maxHP);
        }
    }

    private class Invulnerability : Option
    {
        private uint currentHP;
        private uint maxHP;

        public Invulnerability() : base("Invulnerability", "Set Sora to be invulnerable.",
            Category.Sora, SubCategory.Stats,
            EffectFunction.RepeatAction, durationSeconds: 60)
        { }

        public override bool StartEffect(IPS2Connector connector)
        {
            return connector.Read32LE(StatAddresses.HP, out currentHP)
                && connector.Read32LE(StatAddresses.MaxHP, out maxHP);
        }

        public override bool DoEffect(IPS2Connector connector)
        {
            return connector.Write32LE(StatAddresses.HP, 999)
                && connector.Write32LE(StatAddresses.MaxHP, 999);
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            return connector.Write32LE(StatAddresses.HP, currentHP)
                && connector.Write32LE(StatAddresses.MaxHP, maxHP);
        }
    }

    private class MoneybagsSora : Option
    {
        public MoneybagsSora() : base("Munnybags Sora", "Give Sora 9999 Munny.", Category.Sora, SubCategory.Munny, EffectFunction.TryEffect) { }
        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = connector.Read32LE(MiscAddresses.Munny, out uint munny);

            int newAmount = (int)munny + 9999;

            return success && connector.Write32LE(MiscAddresses.Munny, (uint)newAmount);
        }
    }

    private class RobSora : Option
    {
        public RobSora() : base("Rob Sora", "Take all of Sora's Munny.", Category.Sora, SubCategory.Stats, EffectFunction.TryEffect) { }

        public override bool StartEffect(IPS2Connector connector)
        {
            return connector.Write32LE(MiscAddresses.Munny, 0);
        }
    }

    private class WhoAmI : Option
    {
        public WhoAmI() : base("Who Am I?", "Set Sora to a different character.",
            Category.ModelSwap, SubCategory.None,
            EffectFunction.StartTimed, durationSeconds: 60)
        { }

        private readonly List<int> values =
        [
            CharacterValues.KH1Sora,
            CharacterValues.CardSora,
            CharacterValues.DieSora,
            CharacterValues.LionSora,
            CharacterValues.ChristmasSora,
            CharacterValues.SpaceParanoidsSora,
            CharacterValues.TimelessRiverSora,
            CharacterValues.Roxas,
            CharacterValues.DualwieldRoxas,
            CharacterValues.MickeyRobed,
            CharacterValues.Mickey,
            CharacterValues.Minnie,
            CharacterValues.Donald,
            CharacterValues.Goofy,
            CharacterValues.BirdDonald,
            CharacterValues.TortoiseGoofy,
            // CharacterValues.HalloweenDonald, CharacterValues.HalloweenGoofy, - Causes crash?
            // CharacterValues.ChristmasDonald, CharacterValues.ChristmasGoofy,
            CharacterValues.SpaceParanoidsDonald,
            CharacterValues.SpaceParanoidsGoofy,
            CharacterValues.TimelessRiverDonald,
            CharacterValues.TimelessRiverGoofy,
            CharacterValues.Beast,
            CharacterValues.Mulan,
            CharacterValues.Ping,
            CharacterValues.Hercules,
            CharacterValues.Auron,
            CharacterValues.Aladdin,
            CharacterValues.JackSparrow,
            CharacterValues.HalloweenJack,
            CharacterValues.ChristmasJack,
            CharacterValues.Simba,
            CharacterValues.Tron,
            CharacterValues.ValorFormSora,
            CharacterValues.WisdomFormSora,
            CharacterValues.LimitFormSora,
            CharacterValues.MasterFormSora,
            CharacterValues.FinalFormSora,
            CharacterValues.AntiFormSora
        ];

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;
            ushort randomModel = (ushort)values[new Random().Next(values.Count)];
            Log.Message($"Random Model value: {randomModel}");

            success &= connector.Read16LE(CharacterAddresses.Sora, out ushort currentSora);
            Log.Message($"Sora's current model value: {currentSora}");


            success &= connector.Write16LE(CharacterAddresses.Sora, randomModel);

            success &= connector.Read16LE(CharacterAddresses.Sora, out ushort newSora);
            Log.Message($"Sora's current model value: {newSora}");


            success &= connector.Write16LE(CharacterAddresses.LionSora, randomModel);
            success &= connector.Write16LE(CharacterAddresses.ChristmasSora, randomModel);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsSora, randomModel);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverSora, randomModel);

            // TODO Figure out how to swap to Sora
            //int randomIndex = new Random().Next(values.Count);

            //// Set Valor Form to Random Character so we can activate form
            //connector.Write16LE(ConstantAddresses.ValorFormSora, (ushort)values[randomIndex]);

            //// NEEDS ADDITIONAL WORK AND TESTING

            //connector.Write16LE(ConstantAddresses.ReactionPopup, (ushort)ConstantValues.None);

            //connector.Write16LE(ConstantAddresses.ReactionOption, (ushort)ConstantValues.ReactionValor);

            //connector.Write16LE(ConstantAddresses.ReactionEnable, (ushort)ConstantValues.None);

            //Timer timer = new Timer(250);
            //timer.Elapsed += (obj, args) =>
            //{
            //    connector.Read16LE(ConstantAddresses.ReactionEnable, out ushort value);

            //    if (value == 5) timer.Stop();

            //    connector.Write8(ConstantAddresses.ButtonPress, (byte)ConstantValues.Triangle);
            //};
            //timer.Start();

            return success;
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;
            success &= connector.Write16LE(CharacterAddresses.Sora, (ushort)CharacterValues.Sora);
            success &= connector.Write16LE(CharacterAddresses.LionSora, (ushort)CharacterValues.LionSora);
            success &= connector.Write16LE(CharacterAddresses.ChristmasSora, (ushort)CharacterValues.ChristmasSora);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsSora, (ushort)CharacterValues.SpaceParanoidsSora);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverSora, (ushort)CharacterValues.TimelessRiverSora);

            //connector.Write16LE(ConstantAddresses.ValorFormSora, ConstantValues.ValorFormSora);
            return success;
        }
    }

    private class BackseatDriver : Option
    {
        public BackseatDriver() : base("Backseat Driver", "Trigger one of Sora's different forms.",
            Category.Sora, SubCategory.Drive, EffectFunction.TryEffect)
        { }

        private ushort currentKeyblade;
        private ushort currentValorKeyblade;
        private ushort currentMasterKeyblade;
        private ushort currentFinalKeyblade;

        private readonly List<uint> values =
        [
            ReactionValues.ReactionValor,
            ReactionValues.ReactionWisdom,
            ReactionValues.ReactionLimit,
            ReactionValues.ReactionMaster,
            ReactionValues.ReactionFinal //ConstantValues.ReactionAnti
        ];

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;
            // Get us out of a Drive first if we are in one
            success &= connector.WriteFloat(DriveAddresses.DriveTime, MiscValues.None);

            Thread.Sleep(200);

            int randomIndex = new Random().Next(values.Count);

            success &= connector.Read16LE(EquipmentAddresses.SoraWeaponSlot, out currentKeyblade);

            // Set the current keyblade in the slot for the drive form
            if (randomIndex == 0) // Valor
            {
                success &= connector.Read16LE(EquipmentAddresses.SoraValorWeaponSlot, out currentValorKeyblade);
                
                if (currentValorKeyblade < 0x41 || currentValorKeyblade == 0x81) // 0x81 seems to be a default (maybe just randomizer)
                {
                    success &= connector.Write16LE(EquipmentAddresses.SoraValorWeaponSlot, currentKeyblade);
                }
            }
            else if (randomIndex == 3) // Master
            {
                success &= connector.Read16LE(EquipmentAddresses.SoraMasterWeaponSlot, out currentMasterKeyblade);

                if (currentMasterKeyblade < 0x41 || currentMasterKeyblade == 0x44) // 0x44 seems to be a default (maybe just randomizer)
                {
                    success &= connector.Write16LE(EquipmentAddresses.SoraMasterWeaponSlot, currentKeyblade);
                }
            }
            else if (randomIndex == 4) // Final
            {
                success &= connector.Read16LE(EquipmentAddresses.SoraFinalWeaponSlot, out currentFinalKeyblade);

                if (currentFinalKeyblade < 0x41 || currentFinalKeyblade == 0x45) // 0x45 seems to be a default (maybe just randomizer)
                {
                    success &= connector.Write16LE(EquipmentAddresses.SoraFinalWeaponSlot, currentKeyblade);
                }
            }

            success &= connector.Write16LE(DriveAddresses.ReactionPopup, (ushort)MiscValues.None);
            success &= connector.Write16LE(DriveAddresses.ReactionOption, (ushort)values[randomIndex]);
            success &= connector.Write16LE(DriveAddresses.ReactionEnable, (ushort)MiscValues.None);

            // Might be able to move this to RepeatAction?
            TriggerReaction(connector);

            return success;
        }
    }

    private class WhoAreThey : Option
    {
        public WhoAreThey() : base("Who Are They?", "Set Donald and Goofy to different characters.",
            Category.ModelSwap, SubCategory.None,
            EffectFunction.StartTimed, durationSeconds: 60)
        { }

        private readonly List<int> values =
        [
            CharacterValues.Minnie,
            CharacterValues.Donald,
            CharacterValues.Goofy,
            CharacterValues.BirdDonald,
            CharacterValues.TortoiseGoofy,
            //CharacterValues.HalloweenDonald, CharacterValues.HalloweenGoofy, - Causes crash?
            //CharacterValues.ChristmasDonald, CharacterValues.ChristmasGoofy, 
            CharacterValues.SpaceParanoidsDonald,
            CharacterValues.SpaceParanoidsGoofy,
            CharacterValues.TimelessRiverDonald,
            CharacterValues.TimelessRiverGoofy,
            CharacterValues.Beast,
            CharacterValues.Mulan,
            CharacterValues.Ping,
            CharacterValues.Hercules,
            CharacterValues.Auron,
            CharacterValues.Aladdin,
            CharacterValues.JackSparrow,
            CharacterValues.HalloweenJack,
            CharacterValues.ChristmasJack,
            CharacterValues.Simba,
            CharacterValues.Tron,
            CharacterValues.Riku,
            CharacterValues.AxelFriend,
            CharacterValues.LeonFriend,
            CharacterValues.YuffieFriend,
            CharacterValues.TifaFriend,
            CharacterValues.CloudFriend
        ];

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;
            ushort donald = (ushort)values[new Random().Next(values.Count)];
            ushort goofy = (ushort)values[new Random().Next(values.Count)];

            success &= connector.Write16LE(CharacterAddresses.Donald, donald);
            success &= connector.Write16LE(CharacterAddresses.BirdDonald, donald);
            success &= connector.Write16LE(CharacterAddresses.ChristmasDonald, donald);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsDonald, donald);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverDonald, donald);


            success &= connector.Write16LE(CharacterAddresses.Goofy, goofy);
            success &= connector.Write16LE(CharacterAddresses.TortoiseGoofy, goofy);
            success &= connector.Write16LE(CharacterAddresses.ChristmasGoofy, goofy);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsGoofy, goofy);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverGoofy, goofy);

            return success;
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;
            success &= connector.Write16LE(CharacterAddresses.Donald, CharacterValues.Donald);
            success &= connector.Write16LE(CharacterAddresses.BirdDonald, CharacterValues.BirdDonald);
            success &= connector.Write16LE(CharacterAddresses.ChristmasDonald, CharacterValues.ChristmasDonald);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsDonald, CharacterValues.SpaceParanoidsDonald);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverDonald, CharacterValues.TimelessRiverDonald);


            success &= connector.Write16LE(CharacterAddresses.Goofy, CharacterValues.Goofy);
            success &= connector.Write16LE(CharacterAddresses.TortoiseGoofy, CharacterValues.TortoiseGoofy);
            success &= connector.Write16LE(CharacterAddresses.ChristmasGoofy, CharacterValues.ChristmasGoofy);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsGoofy, CharacterValues.SpaceParanoidsGoofy);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverGoofy, CharacterValues.TimelessRiverGoofy);

            return success;
        }
    }

    private class SlowgaSora : Option
    {
        public SlowgaSora() : base("Slowga Sora", "Set Sora's Speed to be super slow.",
            Category.Sora, SubCategory.None,
            EffectFunction.StartTimed, durationSeconds: 30)
        { }

        private uint speed;
        private uint speedAlt = 0;

        public override bool StartEffect(IPS2Connector connector)
        {
            return connector.Read32LE(StatAddresses.Speed, out speed)
                && connector.Write32LE(StatAddresses.Speed, SpeedValues.SlowDownx2);
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            return connector.Write32LE(StatAddresses.Speed, speed);
        }
    }

    private class HastegaSora : Option
    {
        public HastegaSora() : base("Hastega Sora", "Set Sora's Speed to be super fast.",
            Category.Sora, SubCategory.Stats,
            EffectFunction.StartTimed, durationSeconds: 30)
        { }

        private uint speed;
        private uint speedAlt = 0;

        public override bool StartEffect(IPS2Connector connector)
        {
            return connector.Read32LE(StatAddresses.Speed, out speed)
                && connector.Write32LE(StatAddresses.Speed, SpeedValues.SpeedUpx2);
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            return connector.Write32LE(StatAddresses.Speed, speed);
        }
    }

    // NEEDS WORK -- DOESNT SEEM TO DO ANYTHING
    private class SpaceJump : Option
    {
        public SpaceJump() : base("Space Jump", "Give Sora the ability to Space Jump.",
            Category.Sora, SubCategory.None,
            EffectFunction.StartTimed, durationSeconds: 60)
        { }

        private uint jump;

        public override bool StartEffect(IPS2Connector connector)
        {
            // Store original jump amount for the reset
            return connector.Read32LE(MiscAddresses.JumpAmount, out jump)
                && connector.Write32LE(MiscAddresses.JumpAmount, 0);
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            return connector.Write32LE(MiscAddresses.JumpAmount, jump);
        }
    }

    private class TinyWeapon : Option
    {
        public TinyWeapon() : base("Tiny Weapon", "Set Sora's Weapon size to be tiny.",
            Category.Sora, SubCategory.None,
            EffectFunction.StartTimed, durationSeconds: 60)
        { }

        public override bool StartEffect(IPS2Connector connector)
        {
            return connector.Write32LE(MiscAddresses.WeaponSize, WeaponValues.TinyWeapon);
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            return connector.Write32LE(MiscAddresses.WeaponSize, WeaponValues.NormalWeapon);
        }
    }

    private class GiantWeapon : Option
    {
        public GiantWeapon() : base("Giant Weapon", "Set Sora's Weapon size to be huge.",
            Category.Sora, SubCategory.None,
            EffectFunction.StartTimed, durationSeconds: 60)
        { }

        public override bool StartEffect(IPS2Connector connector)
        {
            return connector.Write32LE(MiscAddresses.WeaponSize, WeaponValues.BigWeapon);
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            return connector.Write32LE(MiscAddresses.WeaponSize, WeaponValues.NormalWeapon);
        }
    }

    private class Struggling : Option
    {
        public Struggling() : base("Struggling", "Change Sora's weapon to the Struggle Bat.",
            Category.Sora, SubCategory.Weapon,
            EffectFunction.StartTimed, durationSeconds: 60)
        { }

        private ushort? currentKeyblade = null;

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;
            success &= connector.Read16LE(EquipmentAddresses.SoraWeaponSlot, out ushort currKeyblade);
            // Only override the value that will be reset to at the end if there isn't a value currently queued for reset
            if (currentKeyblade == null) {
                currentKeyblade = currKeyblade;
            }
            success &= connector.Write16LE(EquipmentAddresses.SoraWeaponSlot, KeybladeValues.StruggleBat);
            return success;
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;
            if (currentKeyblade != null) {
                success &= connector.Write16LE(EquipmentAddresses.SoraWeaponSlot, (ushort)currentKeyblade);
                // Reset current keyblade so that the next invocation of the effect can overwrite it
                currentKeyblade = null;
            }

            return success;
        }
    }

    private class HostileParty : Option
    {
        public HostileParty() : base("Hostile Party", "Set Donald and Goofy to random enemies.",
            Category.ModelSwap, SubCategory.Enemy,
            EffectFunction.StartTimed, durationSeconds: 60)
        { }

        private readonly List<int> values =
        [
            CharacterValues.LeonEnemy,
            CharacterValues.YuffieEnemy,
            CharacterValues.TifaEnemy,
            CharacterValues.CloudEnemy,
            CharacterValues.Xemnas,
            CharacterValues.Xigbar,
            CharacterValues.Xaldin,
            CharacterValues.Vexen,
            CharacterValues.VexenAntiSora,
            CharacterValues.Lexaeus,
            CharacterValues.Zexion,
            CharacterValues.Saix,
            CharacterValues.AxelEnemy,
            CharacterValues.Demyx,
            CharacterValues.DemyxWaterClone,
            CharacterValues.Luxord,
            CharacterValues.Marluxia,
            CharacterValues.Larxene,
            CharacterValues.RoxasEnemy,
            CharacterValues.RoxasShadow,
            CharacterValues.Sephiroth,
            CharacterValues.LingeringWill
        ];

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;

            ushort donald = (ushort)values[new Random().Next(values.Count)];
            ushort goofy = (ushort)values[new Random().Next(values.Count)];

            success &= connector.Write16LE(CharacterAddresses.Donald, donald);
            connector.Write16LE(CharacterAddresses.BirdDonald, donald);
            connector.Write16LE(CharacterAddresses.ChristmasDonald, donald);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsDonald, donald);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverDonald, donald);


            success &= connector.Write16LE(CharacterAddresses.Goofy, goofy);
            success &= connector.Write16LE(CharacterAddresses.TortoiseGoofy, goofy);
            success &= connector.Write16LE(CharacterAddresses.ChristmasGoofy, goofy);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsGoofy, goofy);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverGoofy, goofy);

            return success;
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;
            success &= connector.Write16LE(CharacterAddresses.Donald, CharacterValues.Donald);
            success &= connector.Write16LE(CharacterAddresses.BirdDonald, CharacterValues.BirdDonald);
            success &= connector.Write16LE(CharacterAddresses.ChristmasDonald, CharacterValues.ChristmasDonald);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsDonald, CharacterValues.SpaceParanoidsDonald);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverDonald, CharacterValues.TimelessRiverDonald);


            success &= connector.Write16LE(CharacterAddresses.Goofy, CharacterValues.Goofy);
            success &= connector.Write16LE(CharacterAddresses.TortoiseGoofy, CharacterValues.TortoiseGoofy);
            success &= connector.Write16LE(CharacterAddresses.ChristmasGoofy, CharacterValues.ChristmasGoofy);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsGoofy, CharacterValues.SpaceParanoidsGoofy);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverGoofy, CharacterValues.TimelessRiverGoofy);

            return success;
        }
    }

    private class IAmDarkness : Option
    {
        public IAmDarkness() : base("I Am Darkness", "Change Sora to Antiform Sora.",
            Category.ModelSwap, SubCategory.None, EffectFunction.TryEffect)
        { }

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;

            // Get us out of a Drive first if we are in one
            success &= connector.WriteFloat(DriveAddresses.DriveTime, MiscValues.None);
            Thread.Sleep(200);

            success &= connector.Write16LE(DriveAddresses.ReactionPopup, (ushort)MiscValues.None);

            success &= connector.Write16LE(DriveAddresses.ReactionOption, (ushort)ReactionValues.ReactionAnti);

            success &= connector.Write16LE(DriveAddresses.ReactionEnable, (ushort)MiscValues.None);

            TriggerReaction(connector);
            return success;
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            return true;
        }
    }

    // NEEDS IMPLEMENTATION
    private class KillSora : Option
    {
        public KillSora() : base("Kill Sora", "Instantly Kill Sora.",
            Category.Sora, SubCategory.Stats, EffectFunction.TryEffect)
        { }

        public override bool StartEffect(IPS2Connector connector)
        {
            throw new NotImplementedException();
        }
    }

    // NEEDS IMPLEMENTATION
    private class RandomizeControls : Option
    {
        public RandomizeControls() : base("Randomize Controls", "Randomize the controls to the game.",
            Category.None, SubCategory.None, EffectFunction.StartTimed)
        { }

        private Dictionary<uint, uint> controls = new()
        {
            //{ ConstantAddresses.Control, 0 },
        };

        public override bool StartEffect(IPS2Connector connector)
        {
            throw new NotImplementedException();
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            throw new NotImplementedException();
        }
    }

    private class ShuffleShortcuts : Option
    {
        public ShuffleShortcuts() : base("Shuffle Shortcuts", "Set Sora's Shortcuts to random commands.",
            Category.Sora, SubCategory.None,
            EffectFunction.StartTimed, durationSeconds: 60)
        { }

        private readonly Random random = new();
        private readonly Dictionary<int, Tuple<int, int>> values = new()
            {
                { BaseItemAddresses.Potion, new Tuple<int, int>(QuickSlotValues.PotionQuickSlotValue, ItemValues.Potion) }, { BaseItemAddresses.HiPotion, new Tuple<int, int>(QuickSlotValues.HiPotionQuickSlotValue, ItemValues.HiPotion) },
                { BaseItemAddresses.MegaPotion, new Tuple<int, int>(QuickSlotValues.MegaPotionQuickSlotValue, ItemValues.MegaPotion) }, { BaseItemAddresses.Ether, new Tuple<int, int>(QuickSlotValues.EtherQuickSlotValue, ItemValues.Ether) },
                { BaseItemAddresses.MegaEther, new Tuple<int, int>(QuickSlotValues.MegaEtherQuickSlotValue, ItemValues.MegaEther) }, { BaseItemAddresses.Elixir, new Tuple<int, int>(QuickSlotValues.ElixirQuickSlotValue, ItemValues.Elixir) },
                { BaseItemAddresses.Megalixir, new Tuple<int, int>(QuickSlotValues.MegalixirQuickSlotValue, ItemValues.Megalixir) }, { MagicAddresses.Fire, new Tuple<int, int>(QuickSlotValues.FireQuickSlotValue, MagicValues.Fire) },
                { MagicAddresses.Blizzard, new Tuple<int, int>(QuickSlotValues.BlizzardQuickSlotValue, MagicValues.Blizzard) }, { MagicAddresses.Thunder, new Tuple<int, int>(QuickSlotValues.ThunderQuickSlotValue, MagicValues.Thunder) },
                { MagicAddresses.Cure, new Tuple<int, int>(QuickSlotValues.CureQuickSlotValue, MagicValues.Cure) }, { MagicAddresses.Reflect, new Tuple<int, int>(QuickSlotValues.ReflectQuickSlotValue, MagicValues.Reflect) },
                { MagicAddresses.Magnet, new Tuple<int, int>(QuickSlotValues.MagnetQuickSlotValue, MagicValues.Magnet) }
            };

        private ushort shortcut1;
        private ushort shortcut2;
        private ushort shortcut3;
        private ushort shortcut4;

        private ulong shortcut1_set;
        private ulong shortcut2_set;
        private ulong shortcut3_set;
        private ulong shortcut4_set;

        private (int, bool) CheckQuickSlot(IPS2Connector connector, int key, Tuple<int, int> value, int shortcutNumber)
        {
            bool success = true;
            if (key != MagicAddresses.Fire && key != MagicAddresses.Blizzard && key != MagicAddresses.Thunder &&
                key != MagicAddresses.Cure && key != MagicAddresses.Reflect && key != MagicAddresses.Magnet)
            {
                success &= connector.Read16LE((ulong)key, out ushort itemValue);

                success &= connector.Write16LE((ulong)key, (ushort)(itemValue + 1));

                switch (shortcutNumber)
                {
                    case 1:
                        shortcut1_set = (ulong)key;
                        success &= connector.Write16LE(EquipmentAddresses.SoraItemSlot1, (ushort)(value.Item2));
                        break;
                    case 2:
                        shortcut2_set = (ulong)key;
                        success &= connector.Write16LE(EquipmentAddresses.SoraItemSlot2, (ushort)(value.Item2));
                        break;
                    case 3:
                        shortcut3_set = (ulong)key;
                        success &= connector.Write16LE(EquipmentAddresses.SoraItemSlot3, (ushort)(value.Item2));
                        break;
                    case 4:
                        shortcut4_set = (ulong)key;
                        success &= connector.Write16LE(EquipmentAddresses.SoraItemSlot4, (ushort)(value.Item2));
                        break;
                }

                return (value.Item1, success);
            }

            success &= connector.Read8((ulong)key, out byte byteValue);

            if (byteValue == 0)
            {
                success &= connector.Write8((ulong)key, (byte)value.Item2);

                switch (shortcutNumber)
                {
                    case 1:
                        shortcut1_set = (ulong)key;
                        break;
                    case 2:
                        shortcut2_set = (ulong)key;
                        break;
                    case 3:
                        shortcut3_set = (ulong)key;
                        break;
                    case 4:
                        shortcut4_set = (ulong)key;
                        break;
                }
            }

            if (key == MagicAddresses.Fire)
                return (QuickSlotValues.FireQuickSlotValue, success);
            if (key == MagicAddresses.Blizzard)
                return (QuickSlotValues.BlizzardQuickSlotValue, success);
            if (key == MagicAddresses.Thunder)
                return (QuickSlotValues.ThunderQuickSlotValue, success);
            if (key == MagicAddresses.Cure)
                return (QuickSlotValues.CureQuickSlotValue, success);
            if (key == MagicAddresses.Reflect)
                return (QuickSlotValues.ReflectQuickSlotValue, success);
            if (key == MagicAddresses.Magnet)
                return (QuickSlotValues.MagnetQuickSlotValue, success);

            return (MiscValues.None, success);
        }

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;
            // Save the values before the shuffle
            success &= connector.Read16LE(EquipmentAddresses.SoraQuickMenuSlot1, out shortcut1);
            success &= connector.Read16LE(EquipmentAddresses.SoraQuickMenuSlot2, out shortcut2);
            success &= connector.Read16LE(EquipmentAddresses.SoraQuickMenuSlot3, out shortcut3);
            success &= connector.Read16LE(EquipmentAddresses.SoraQuickMenuSlot4, out shortcut4);

            int key1 = values.Keys.ToList()[random.Next(values.Keys.Count)];
            int key2 = values.Keys.ToList()[random.Next(values.Keys.Count)];
            int key3 = values.Keys.ToList()[random.Next(values.Keys.Count)];
            int key4 = values.Keys.ToList()[random.Next(values.Keys.Count)];

            (int value1, bool success1) = CheckQuickSlot(connector, key1, values[key1], 1);
            (int value2, bool success2) = CheckQuickSlot(connector, key2, values[key2], 2);
            (int value3, bool success3) = CheckQuickSlot(connector, key3, values[key3], 3);
            (int value4, bool success4) = CheckQuickSlot(connector, key4, values[key4], 4);

            success &= success1 && success2 && success3 && success4;

            success &= connector.Write16LE(EquipmentAddresses.SoraQuickMenuSlot1, (ushort)value1);
            success &= connector.Write16LE(EquipmentAddresses.SoraQuickMenuSlot2, (ushort)value2);
            success &= connector.Write16LE(EquipmentAddresses.SoraQuickMenuSlot3, (ushort)value3);
            success &= connector.Write16LE(EquipmentAddresses.SoraQuickMenuSlot4, (ushort)value4);

            return success;
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;
            success &= connector.Write16LE(EquipmentAddresses.SoraQuickMenuSlot1, shortcut1);
            success &= connector.Write16LE(EquipmentAddresses.SoraQuickMenuSlot2, shortcut2);
            success &= connector.Write16LE(EquipmentAddresses.SoraQuickMenuSlot3, shortcut3);
            success &= connector.Write16LE(EquipmentAddresses.SoraQuickMenuSlot4, shortcut4);

            if (shortcut1_set != 0)
            {
                success &= connector.Write8(shortcut1_set, 0);
                shortcut1_set = 0;
            }
            if (shortcut2_set != 0)
            {
                success &= connector.Write8(shortcut2_set, 0);
                shortcut2_set = 0;
            }
            if (shortcut3_set != 0)
            {
                success &= connector.Write8(shortcut3_set, 0);
                shortcut3_set = 0;
            }
            if (shortcut4_set != 0)
            {
                success &= connector.Write8(shortcut4_set, 0);
                shortcut4_set = 0;
            }

            return success;
        }
    }

    private class GrowthSpurt : Option
    {
        public GrowthSpurt() : base("Growth Spurt", "Give Sora Max Growth abilities.", Category.Sora, SubCategory.Stats,
            EffectFunction.StartTimed, durationSeconds: 60)
        { }

        private uint startAddress;

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;
            Log.Message("GrowthSpurt");
            // Sora has 148 (maybe divided by 2?) slots available for abilities
            for (uint i = EquipmentAddresses.SoraAbilityStart; i < (EquipmentAddresses.SoraAbilityStart + 148); i += 2)
            {
                success &= connector.Read8(i, out byte value);

                if (value != 0) continue;

                startAddress = i;

                success &= connector.Write8(startAddress, (byte)AbilityValues.HighJumpMax);
                success &= connector.Write8(startAddress + 1, 0x80);

                success &= connector.Write8(startAddress + 2, (byte)AbilityValues.QuickRunMax);
                success &= connector.Write8(startAddress + 3, 0x80);

                success &= connector.Write8(startAddress + 4, (byte)AbilityValues.DodgeRollMax);
                success &= connector.Write8(startAddress + 5, 0x82);

                success &= connector.Write8(startAddress + 6, (byte)AbilityValues.AerialDodgeMax);
                success &= connector.Write8(startAddress + 7, 0x80);

                success &= connector.Write8(startAddress + 8, (byte)AbilityValues.GlideMax);
                success &= connector.Write8(startAddress + 9, 0x80);

                break;
            }

            return success;
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;

            success &= connector.Write8(startAddress, 0);
            success &= connector.Write8(startAddress + 1, 0);

            success &= connector.Write8(startAddress + 2, 0);
            success &= connector.Write8(startAddress + 3, 0);

            success &= connector.Write8(startAddress + 4, 0);
            success &= connector.Write8(startAddress + 5, 0);

            success &= connector.Write8(startAddress + 6, 0);
            success &= connector.Write8(startAddress + 7, 0);

            success &= connector.Write8(startAddress + 8, 0);
            success &= connector.Write8(startAddress + 9, 0);

            return success;
        }
    }

    private class ExpertMagician : Option
    {
        public ExpertMagician() : base("Expert Magician", "Give Sora Max Magic and lower the cost of Magic.",
            Category.Sora, SubCategory.Stats,
            EffectFunction.StartTimed, durationSeconds: 30)
        { }

        private byte fire;
        private byte blizzard;
        private byte thunder;
        private byte cure;
        private byte reflect;
        private byte magnet;

        private byte fireCost;
        private byte blizzardCost;
        private byte thunderCost;
        private byte cureCost;
        private byte reflectCost;
        private byte magnetCost;

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;

            // Save Magic
            success &= connector.Read8((ulong)MagicAddresses.Fire, out fire);
            success &= connector.Read8((ulong)MagicAddresses.Blizzard, out blizzard);
            success &= connector.Read8((ulong)MagicAddresses.Thunder, out thunder);
            success &= connector.Read8((ulong)MagicAddresses.Cure, out cure);
            success &= connector.Read8((ulong)MagicAddresses.Reflect, out reflect);
            success &= connector.Read8((ulong)MagicAddresses.Magnet, out magnet);

            // Write Max Magic
            success &= connector.Write8((ulong)MagicAddresses.Fire, (byte)MagicValues.Firaga);
            success &= connector.Write8((ulong)MagicAddresses.Blizzard, (byte)MagicValues.Blizzaga);
            success &= connector.Write8((ulong)MagicAddresses.Thunder, (byte)MagicValues.Thundaga);
            success &= connector.Write8((ulong)MagicAddresses.Cure, (byte)MagicValues.Curaga);
            success &= connector.Write8((ulong)MagicAddresses.Reflect, (byte)MagicValues.Reflega);
            success &= connector.Write8((ulong)MagicAddresses.Magnet, (byte)MagicValues.Magnega);

            // Save Magic Costs
            success &= connector.Read8(MPCostAddresses.FiragaCost, out fireCost);
            success &= connector.Read8(MPCostAddresses.BlizzagaCost, out blizzardCost);
            success &= connector.Read8(MPCostAddresses.ThundagaCost, out thunderCost);
            success &= connector.Read8(MPCostAddresses.CuragaCost, out cureCost);
            success &= connector.Read8(MPCostAddresses.ReflegaCost, out reflectCost);
            success &= connector.Read8(MPCostAddresses.MagnegaCost, out magnetCost);

            // Write Magic Costs
            success &= connector.Write8(MPCostAddresses.FiragaCost, 0x1);
            success &= connector.Write8(MPCostAddresses.BlizzagaCost, 0x2);
            success &= connector.Write8(MPCostAddresses.ThundagaCost, 0x3);
            success &= connector.Write8(MPCostAddresses.CuragaCost, 0x10);
            success &= connector.Write8(MPCostAddresses.ReflegaCost, 0x6);
            success &= connector.Write8(MPCostAddresses.MagnegaCost, 0x5);

            return success;
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;
            // Write back saved Magic
            success &= connector.Write8((ulong)MagicAddresses.Fire, fire);
            success &= connector.Write8((ulong)MagicAddresses.Blizzard, blizzard);
            success &= connector.Write8((ulong)MagicAddresses.Thunder, thunder);
            success &= connector.Write8((ulong)MagicAddresses.Cure, cure);
            success &= connector.Write8((ulong)MagicAddresses.Reflect, reflect);
            success &= connector.Write8((ulong)MagicAddresses.Magnet, magnet);

            // Write back saved Magic Costs
            success &= connector.Write8(MPCostAddresses.FiragaCost, fireCost);
            success &= connector.Write8(MPCostAddresses.BlizzagaCost, blizzardCost);
            success &= connector.Write8(MPCostAddresses.ThundagaCost, thunderCost);
            success &= connector.Write8(MPCostAddresses.CuragaCost, cureCost);
            success &= connector.Write8(MPCostAddresses.ReflegaCost, reflectCost);
            success &= connector.Write8(MPCostAddresses.MagnegaCost, magnetCost);

            return success;
        }
    }

    private class AmnesiacMagician : Option
    {
        public AmnesiacMagician() : base("Amnesiac Magician", "Take away all of Sora's Magic.",
            Category.Sora, SubCategory.Stats,
            EffectFunction.StartTimed, durationSeconds: 60)
        { }

        private byte fire;
        private byte blizzard;
        private byte thunder;
        private byte cure;
        private byte reflect;
        private byte magnet;

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;
            success &= connector.Read8((ulong)MagicAddresses.Fire, out fire);
            success &= connector.Read8((ulong)MagicAddresses.Blizzard, out blizzard);
            success &= connector.Read8((ulong)MagicAddresses.Thunder, out thunder);
            success &= connector.Read8((ulong)MagicAddresses.Cure, out cure);
            success &= connector.Read8((ulong)MagicAddresses.Reflect, out reflect);
            success &= connector.Read8((ulong)MagicAddresses.Magnet, out magnet);

            success &= connector.Write8((ulong)MagicAddresses.Fire, (byte)MiscValues.None);
            success &= connector.Write8((ulong)MagicAddresses.Blizzard, (byte)MiscValues.None);
            success &= connector.Write8((ulong)MagicAddresses.Thunder, (byte)MiscValues.None);
            success &= connector.Write8((ulong)MagicAddresses.Cure, (byte)MiscValues.None);
            success &= connector.Write8((ulong)MagicAddresses.Reflect, (byte)MiscValues.None);
            success &= connector.Write8((ulong)MagicAddresses.Magnet, (byte)MiscValues.None);

            return success;
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;
            success &= connector.Write8((ulong)MagicAddresses.Fire, fire);
            success &= connector.Write8((ulong)MagicAddresses.Blizzard, blizzard);
            success &= connector.Write8((ulong)MagicAddresses.Thunder, thunder);
            success &= connector.Write8((ulong)MagicAddresses.Cure, cure);
            success &= connector.Write8((ulong)MagicAddresses.Reflect, reflect);
            success &= connector.Write8((ulong)MagicAddresses.Magnet, magnet);
            return success;
        }
    }

    private class Itemaholic : Option
    {
        public Itemaholic() : base("Itemaholic", "Fill Sora's inventory with all items, accessories, armor and weapons.",
            Category.Sora, SubCategory.Stats,
            EffectFunction.StartTimed, durationSeconds: 60)
        { }

        // Used to store all the information about what held items Sora had before
        private readonly Dictionary<uint, byte> items = new()
            {
                { (uint)BaseItemAddresses.Potion, 0 }, { (uint)BaseItemAddresses.HiPotion, 0 }, { (uint)BaseItemAddresses.Ether, 0 },
                { (uint)BaseItemAddresses.MegaPotion, 0 }, { (uint)BaseItemAddresses.MegaEther, 0 }, { (uint)BaseItemAddresses.Elixir, 0 },
                { (uint)BaseItemAddresses.Megalixir, 0 }, { (uint)BaseItemAddresses.Tent, 0 }, { (uint)BaseItemAddresses.DriveRecovery, 0 },
                { (uint)BaseItemAddresses.HighDriveRecovery, 0 }, { (uint)BaseItemAddresses.PowerBoost, 0 }, { (uint)BaseItemAddresses.MagicBoost, 0 },
                { (uint)BaseItemAddresses.DefenseBoost, 0 }, { (uint)BaseItemAddresses.APBoost, 0 },

                { AccessoryAddresses.AbilityRing, 0 }, { AccessoryAddresses.EngineersRing, 0 }, { AccessoryAddresses.TechniciansRing, 0 },
                { AccessoryAddresses.ExpertsRing, 0 }, { AccessoryAddresses.SardonyxRing, 0 }, { AccessoryAddresses.TourmalineRing, 0 },
                { AccessoryAddresses.AquamarineRing, 0 }, { AccessoryAddresses.GarnetRing, 0 }, { AccessoryAddresses.DiamondRing, 0 },
                { AccessoryAddresses.SilverRing, 0 },{ AccessoryAddresses.GoldRing, 0 }, { AccessoryAddresses.PlatinumRing, 0 },
                { AccessoryAddresses.MythrilRing, 0 }, { AccessoryAddresses.OrichalcumRing, 0 }, { AccessoryAddresses.MastersRing, 0 },
                { AccessoryAddresses.MoonAmulet, 0 }, { AccessoryAddresses.StarCharm, 0 }, { AccessoryAddresses.SkillRing, 0 },
                { AccessoryAddresses.SkillfulRing, 0 }, { AccessoryAddresses.SoldierEarring, 0 }, { AccessoryAddresses.FencerEarring, 0 },
                { AccessoryAddresses.MageEarring, 0 }, { AccessoryAddresses.SlayerEarring, 0 }, { AccessoryAddresses.CosmicRing, 0 },
                { AccessoryAddresses.Medal, 0 }, { AccessoryAddresses.CosmicArts, 0 }, { AccessoryAddresses.ShadowArchive, 0 },
                { AccessoryAddresses.ShadowArchivePlus, 0 }, { AccessoryAddresses.LuckyRing, 0 }, { AccessoryAddresses.FullBloom, 0 },
                { AccessoryAddresses.FullBloomPlus, 0 }, { AccessoryAddresses.DrawRing, 0 }, { AccessoryAddresses.ExecutivesRing, 0 },

                { ArmorAddresses.ElvenBandana, 0 }, { ArmorAddresses.DivineBandana, 0 }, { ArmorAddresses.PowerBand, 0 },
                { ArmorAddresses.BusterBand, 0 }, { ArmorAddresses.ProtectBelt, 0 }, { ArmorAddresses.GaiaBelt, 0 },
                { ArmorAddresses.CosmicBelt, 0 }, { ArmorAddresses.ShockCharm, 0 }, { ArmorAddresses.ShockCharmPlus, 0 },
                { ArmorAddresses.FireBangle, 0 }, { ArmorAddresses.FiraBangle, 0 }, { ArmorAddresses.FiragaBangle, 0 },
                { ArmorAddresses.FiragunBangle, 0 }, { ArmorAddresses.BlizzardArmlet, 0 }, { ArmorAddresses.BlizzaraArmlet, 0 },
                { ArmorAddresses.BlizzagaArmlet, 0 }, { ArmorAddresses.BlizzagunArmlet, 0 }, { ArmorAddresses.ThunderTrinket, 0 },
                { ArmorAddresses.ThundaraTrinket, 0 }, { ArmorAddresses.ThundagaTrinket, 0 }, { ArmorAddresses.ThundagunTrinket, 0 },
                { ArmorAddresses.ShadowAnklet, 0 }, { ArmorAddresses.DarkAnklet, 0 }, { ArmorAddresses.MidnightAnklet, 0 },
                { ArmorAddresses.ChaosAnklet, 0 }, { ArmorAddresses.AbasChain, 0 }, { ArmorAddresses.AegisChain, 0 },
                { ArmorAddresses.CosmicChain, 0 }, { ArmorAddresses.Acrisius, 0 }, { ArmorAddresses.AcrisiusPlus, 0 },
                { ArmorAddresses.PetiteRibbon, 0 }, { ArmorAddresses.Ribbon, 0 }, { ArmorAddresses.GrandRibbon, 0 },
                { ArmorAddresses.ChampionBelt, 0 },

                { KeybladeAddresses.KingdomKey, 0 }, { KeybladeAddresses.Oathkeeper, 0 }, { KeybladeAddresses.Oblivion, 0 },
                { KeybladeAddresses.DetectionSaber, 0 }, { KeybladeAddresses.FrontierOfUltima, 0 }, { KeybladeAddresses.StarSeeker, 0 },
                { KeybladeAddresses.HiddenDragon, 0 }, { KeybladeAddresses.HerosCrest, 0 }, { KeybladeAddresses.Monochrome, 0 },
                { KeybladeAddresses.FollowTheWind, 0 }, { KeybladeAddresses.CircleOfLife, 0 }, { KeybladeAddresses.PhotonDebugger, 0 },
                { KeybladeAddresses.GullWing, 0 }, { KeybladeAddresses.RumblingRose, 0 }, { KeybladeAddresses.GuardianSoul, 0 },
                { KeybladeAddresses.WishingLamp, 0 }, { KeybladeAddresses.DecisivePumpkin, 0 }, { KeybladeAddresses.SleepingLion, 0 },
                { KeybladeAddresses.SweetMemories, 0 }, { KeybladeAddresses.MysteriousAbyss, 0 }, { KeybladeAddresses.BondOfFlame, 0 },
                { KeybladeAddresses.FatalCrest, 0 }, { KeybladeAddresses.Fenrir, 0 }, { KeybladeAddresses.UltimaWeapon, 0 },
                { KeybladeAddresses.TwoBecomeOne, 0 }, { KeybladeAddresses.WinnersProof, 0 },
            };

        private readonly Dictionary<uint, ushort> slots = new()
            {
                { EquipmentAddresses.SoraWeaponSlot, 0 }, { EquipmentAddresses.SoraValorWeaponSlot, 0 }, { EquipmentAddresses.SoraMasterWeaponSlot, 0 },
                { EquipmentAddresses.SoraFinalWeaponSlot, 0 }, { EquipmentAddresses.SoraArmorSlot1, 0 }, { EquipmentAddresses.SoraArmorSlot2, 0 },
                { EquipmentAddresses.SoraArmorSlot3, 0 }, { EquipmentAddresses.SoraArmorSlot4, 0 }, { EquipmentAddresses.SoraAccessorySlot1, 0 },
                { EquipmentAddresses.SoraAccessorySlot2, 0 }, { EquipmentAddresses.SoraAccessorySlot3, 0 }, { EquipmentAddresses.SoraAccessorySlot4, 0 },
                { EquipmentAddresses.SoraItemSlot1, 0 }, { EquipmentAddresses.SoraItemSlot2, 0 }, { EquipmentAddresses.SoraItemSlot3, 0 },
                { EquipmentAddresses.SoraItemSlot4, 0 }, { EquipmentAddresses.SoraItemSlot5, 0 }, { EquipmentAddresses.SoraItemSlot6, 0 },
                { EquipmentAddresses.SoraItemSlot7, 0 }, { EquipmentAddresses.SoraItemSlot8, 0 }
            };

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;
            // Save all current items, before writing max value to them
            foreach (var (itemAddress, _) in items)
            {
                success &= connector.Read8(itemAddress, out byte itemCount);

                items[itemAddress] = itemCount;

                success &= connector.Write8(itemAddress, byte.MaxValue);
            }

            // Save all current slots
            foreach (var (slotAddress, _) in slots)
            {
                success &= connector.Read16LE(slotAddress, out ushort slotValue);

                slots[slotAddress] = slotValue;
            }

            return success;
        }

        // DO WE WANT TO REMOVE THESE AFTER OR JUST HAVE THIS AS A ONE TIME REDEEM?
        // We'll put things back to how they were after a timer
        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;
            // Write back all saved items
            foreach (var (itemAddress, itemCount) in items)
            {
                success &= connector.Write8(itemAddress, itemCount);
            }

            // Write back all saved slots
            foreach (var (slotAddress, slotValue) in slots)
            {
                success &= connector.Write16LE(slotAddress, slotValue);
            }

            return success;
        }
    }

    private class SpringCleaning : Option
    {
        public SpringCleaning() : base("Spring Cleaning", "Remove all items, accessories, armor and weapons from Sora's inventory.",
            Category.Sora, SubCategory.Stats,
            EffectFunction.TryEffect, durationSeconds: 60)
        { }

        // Used to store all the information about what held items Sora had before
        private readonly Dictionary<uint, byte> items = new()
            {
                { (uint)BaseItemAddresses.Potion, 0 }, { (uint)BaseItemAddresses.HiPotion, 0 }, { (uint)BaseItemAddresses.Ether, 0 },
                { (uint)BaseItemAddresses.MegaPotion, 0 }, { (uint)BaseItemAddresses.MegaEther, 0 }, { (uint)BaseItemAddresses.Elixir, 0 },
                { (uint)BaseItemAddresses.Megalixir, 0 }, { (uint)BaseItemAddresses.Tent, 0 }, { (uint)BaseItemAddresses.DriveRecovery, 0 },
                { (uint)BaseItemAddresses.HighDriveRecovery, 0 }, { (uint)BaseItemAddresses.PowerBoost, 0 }, { (uint)BaseItemAddresses.MagicBoost, 0 },
                { (uint)BaseItemAddresses.DefenseBoost, 0 }, { (uint)BaseItemAddresses.APBoost, 0 },

                { AccessoryAddresses.AbilityRing, 0 }, { AccessoryAddresses.EngineersRing, 0 }, { AccessoryAddresses.TechniciansRing, 0 },
                { AccessoryAddresses.ExpertsRing, 0 }, { AccessoryAddresses.SardonyxRing, 0 }, { AccessoryAddresses.TourmalineRing, 0 },
                { AccessoryAddresses.AquamarineRing, 0 }, { AccessoryAddresses.GarnetRing, 0 }, { AccessoryAddresses.DiamondRing, 0 },
                { AccessoryAddresses.SilverRing, 0 },{ AccessoryAddresses.GoldRing, 0 }, { AccessoryAddresses.PlatinumRing, 0 },
                { AccessoryAddresses.MythrilRing, 0 }, { AccessoryAddresses.OrichalcumRing, 0 }, { AccessoryAddresses.MastersRing, 0 },
                { AccessoryAddresses.MoonAmulet, 0 }, { AccessoryAddresses.StarCharm, 0 }, { AccessoryAddresses.SkillRing, 0 },
                { AccessoryAddresses.SkillfulRing, 0 }, { AccessoryAddresses.SoldierEarring, 0 }, { AccessoryAddresses.FencerEarring, 0 },
                { AccessoryAddresses.MageEarring, 0 }, { AccessoryAddresses.SlayerEarring, 0 }, { AccessoryAddresses.CosmicRing, 0 },
                { AccessoryAddresses.Medal, 0 }, { AccessoryAddresses.CosmicArts, 0 }, { AccessoryAddresses.ShadowArchive, 0 },
                { AccessoryAddresses.ShadowArchivePlus, 0 }, { AccessoryAddresses.LuckyRing, 0 }, { AccessoryAddresses.FullBloom, 0 },
                { AccessoryAddresses.FullBloomPlus, 0 }, { AccessoryAddresses.DrawRing, 0 }, { AccessoryAddresses.ExecutivesRing, 0 },

                { ArmorAddresses.ElvenBandana, 0 }, { ArmorAddresses.DivineBandana, 0 }, { ArmorAddresses.PowerBand, 0 },
                { ArmorAddresses.BusterBand, 0 }, { ArmorAddresses.ProtectBelt, 0 }, { ArmorAddresses.GaiaBelt, 0 },
                { ArmorAddresses.CosmicBelt, 0 }, { ArmorAddresses.ShockCharm, 0 }, { ArmorAddresses.ShockCharmPlus, 0 },
                { ArmorAddresses.FireBangle, 0 }, { ArmorAddresses.FiraBangle, 0 }, { ArmorAddresses.FiragaBangle, 0 },
                { ArmorAddresses.FiragunBangle, 0 }, { ArmorAddresses.BlizzardArmlet, 0 }, { ArmorAddresses.BlizzaraArmlet, 0 },
                { ArmorAddresses.BlizzagaArmlet, 0 }, { ArmorAddresses.BlizzagunArmlet, 0 }, { ArmorAddresses.ThunderTrinket, 0 },
                { ArmorAddresses.ThundaraTrinket, 0 }, { ArmorAddresses.ThundagaTrinket, 0 }, { ArmorAddresses.ThundagunTrinket, 0 },
                { ArmorAddresses.ShadowAnklet, 0 }, { ArmorAddresses.DarkAnklet, 0 }, { ArmorAddresses.MidnightAnklet, 0 },
                { ArmorAddresses.ChaosAnklet, 0 }, { ArmorAddresses.AbasChain, 0 }, { ArmorAddresses.AegisChain, 0 },
                { ArmorAddresses.CosmicChain, 0 }, { ArmorAddresses.Acrisius, 0 }, { ArmorAddresses.AcrisiusPlus, 0 },
                { ArmorAddresses.PetiteRibbon, 0 }, { ArmorAddresses.Ribbon, 0 }, { ArmorAddresses.GrandRibbon, 0 },
                { ArmorAddresses.ChampionBelt, 0 },

                { KeybladeAddresses.KingdomKey, 0 }, { KeybladeAddresses.Oathkeeper, 0 }, { KeybladeAddresses.Oblivion, 0 },
                { KeybladeAddresses.DetectionSaber, 0 }, { KeybladeAddresses.FrontierOfUltima, 0 }, { KeybladeAddresses.StarSeeker, 0 },
                { KeybladeAddresses.HiddenDragon, 0 }, { KeybladeAddresses.HerosCrest, 0 }, { KeybladeAddresses.Monochrome, 0 },
                { KeybladeAddresses.FollowTheWind, 0 }, { KeybladeAddresses.CircleOfLife, 0 }, { KeybladeAddresses.PhotonDebugger, 0 },
                { KeybladeAddresses.GullWing, 0 }, { KeybladeAddresses.RumblingRose, 0 }, { KeybladeAddresses.GuardianSoul, 0 },
                { KeybladeAddresses.WishingLamp, 0 }, { KeybladeAddresses.DecisivePumpkin, 0 }, { KeybladeAddresses.SleepingLion, 0 },
                { KeybladeAddresses.SweetMemories, 0 }, { KeybladeAddresses.MysteriousAbyss, 0 }, { KeybladeAddresses.BondOfFlame, 0 },
                { KeybladeAddresses.FatalCrest, 0 }, { KeybladeAddresses.Fenrir, 0 }, { KeybladeAddresses.UltimaWeapon, 0 },
                { KeybladeAddresses.TwoBecomeOne, 0 }, { KeybladeAddresses.WinnersProof, 0 },
            };

        private readonly Dictionary<uint, ushort> slots = new()
            {
                { EquipmentAddresses.SoraWeaponSlot, 0 }, { EquipmentAddresses.SoraValorWeaponSlot, 0 }, { EquipmentAddresses.SoraMasterWeaponSlot, 0 },
                { EquipmentAddresses.SoraFinalWeaponSlot, 0 }, { EquipmentAddresses.SoraArmorSlot1, 0 }, { EquipmentAddresses.SoraArmorSlot2, 0 },
                { EquipmentAddresses.SoraArmorSlot3, 0 }, { EquipmentAddresses.SoraArmorSlot4, 0 }, { EquipmentAddresses.SoraAccessorySlot1, 0 },
                { EquipmentAddresses.SoraAccessorySlot2, 0 }, { EquipmentAddresses.SoraAccessorySlot3, 0 }, { EquipmentAddresses.SoraAccessorySlot4, 0 },
                { EquipmentAddresses.SoraItemSlot1, 0 }, { EquipmentAddresses.SoraItemSlot2, 0 }, { EquipmentAddresses.SoraItemSlot3, 0 },
                { EquipmentAddresses.SoraItemSlot4, 0 }, { EquipmentAddresses.SoraItemSlot5, 0 }, { EquipmentAddresses.SoraItemSlot6, 0 },
                { EquipmentAddresses.SoraItemSlot7, 0 }, { EquipmentAddresses.SoraItemSlot8, 0 }
            };

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;
            // Save all current items, before writing max value to them
            foreach (var (itemAddress, _) in items)
            {
                success &= connector.Read8(itemAddress, out byte itemCount);

                items[itemAddress] = itemCount;

                success &= connector.Write8(itemAddress, byte.MinValue);
            }

            // Save all current slots
            foreach (var (slotAddress, _) in slots)
            {
                success &= connector.Read16LE(slotAddress, out ushort slotValue);

                slots[slotAddress] = slotValue;

                if (slotAddress != EquipmentAddresses.SoraWeaponSlot && slotAddress != EquipmentAddresses.SoraValorWeaponSlot &&
                    slotAddress != EquipmentAddresses.SoraMasterWeaponSlot && slotAddress != EquipmentAddresses.SoraFinalWeaponSlot)
                {
                    success &= connector.Write16LE(slotAddress, ushort.MinValue);
                }
            }

            return success;
        }

        // DO WE WANT TO REMOVE THESE AFTER OR JUST HAVE THIS AS A ONE TIME REDEEM?
        // We'll put things back to how they were after a timer
        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;
            // Write back all saved items
            foreach (var (itemAddress, itemCount) in items)
            {
                success &= connector.Write8(itemAddress, itemCount);
            }

            // Write back all saved slots
            foreach (var (slotAddress, slotValue) in slots)
            {
                success &= connector.Write16LE(slotAddress, slotValue);
            }
            return success;
        }
    }

    private class SummonChauffeur : Option
    {
        public SummonChauffeur() : base("Summon Chauffeur", "Give all Drives and Summons to Sora.",
            Category.Sora, SubCategory.Summon,
            EffectFunction.StartTimed, durationSeconds: 60)
        { }

        // Used to store all the information about what held items Sora had before
        private readonly Dictionary<uint, byte> drivesSummons = new()
            {
                { DriveAddresses.DriveForms, 0 }, { DriveAddresses.DriveLimitForm, 0 },
                //{ (uint)ConstantAddresses.UkeleleBaseballCharm, 0 }, 
                { SummonAddresses.LampFeatherCharm, 0 },

                { DriveAddresses.Drive, 0 }, { DriveAddresses.MaxDrive, 0 }
            };

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;
            // Save all current items, before writing max value to them
            foreach (var (driveSummon, _) in drivesSummons)
            {
                success &= connector.Read8(driveSummon, out byte value);

                drivesSummons[driveSummon] = value;

                if (driveSummon == DriveAddresses.DriveForms)
                {
                    success &= connector.Write8(driveSummon, 127);
                }
                else if (driveSummon == DriveAddresses.DriveLimitForm)
                {
                    success &= connector.Write8(driveSummon, 8);
                }
                //else if (driveSummon == ConstantAddresses.UkeleleBaseballCharm)
                //{
                //    connector.Write8(value, 9);
                //}
                else if (driveSummon == SummonAddresses.LampFeatherCharm)
                {
                    success &= connector.Write8(driveSummon, 48);
                }
                else
                {
                    success &= connector.Write8(driveSummon, byte.MaxValue);
                }
            }
            return success;
        }

        // DO WE WANT TO REMOVE THESE AFTER OR JUST HAVE THIS AS A ONE TIME REDEEM?
        // We'll put things back to how they were after a timer
        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;
            // Write back all saved items
            foreach (var (driveSummon, value) in drivesSummons)
            {
                success &= connector.Write8(driveSummon, value);
            }
            return success;
        }
    }

    private class SummonTrainer : Option
    {
        public SummonTrainer() : base("Summon Trainer", "Remove all Drives and Summons from Sora.",
            Category.Sora, SubCategory.Summon,
            EffectFunction.StartTimed, durationSeconds: 60)
        { }

        // Used to store all the information about what held items Sora had before
        private readonly Dictionary<uint, byte> drivesSummons = new()
            {
                { DriveAddresses.DriveForms, 0 }, { DriveAddresses.DriveLimitForm, 0 },
                //{ (uint)ConstantAddresses.UkeleleBaseballCharm, 0 }, 
                { SummonAddresses.LampFeatherCharm, 0 },

                { DriveAddresses.Drive, 0 }, { DriveAddresses.MaxDrive, 0 }
            };

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;
            // Save all current items, before writing max value to them
            foreach (var (driveSummon, _) in drivesSummons)
            {
                success &= connector.Read8(driveSummon, out byte value);

                drivesSummons[driveSummon] = value;

                success &= connector.Write8(driveSummon, byte.MinValue);
            }

            return success;
        }

        // DO WE WANT TO REMOVE THESE AFTER OR JUST HAVE THIS AS A ONE TIME REDEEM?
        // Adding in timer
        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;
            // Write back all saved items
            foreach (var (driveSummon, value) in drivesSummons)
            {
                success &= connector.Write8(driveSummon, value);
            }
            return success;
        }
    }

    private class HeroSora : Option
    {
        public HeroSora() : base("Hero Sora", "Set Sora to HERO mode, including Stats, Items, Magic, Drives and Summons.",
            Category.Sora, SubCategory.Stats,
            EffectFunction.StartTimed, durationSeconds: 30)
        {
            expertMagician = new ExpertMagician();
            itemaholic = new Itemaholic();
            summonChauffeur = new SummonChauffeur();
        }

        private byte level;
        private uint hp;
        private uint maxHp;
        private uint mp;
        private uint maxMp;
        private byte strength;
        private byte magic;
        private byte defense;
        private byte ap;

        private readonly ExpertMagician expertMagician;
        private readonly Itemaholic itemaholic;
        private readonly SummonChauffeur summonChauffeur;

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;
            success &= connector.Read8(StatAddresses.Level, out level);
            success &= connector.Read32LE(StatAddresses.HP, out hp);
            success &= connector.Read32LE(StatAddresses.MaxHP, out maxHp);
            success &= connector.Read32LE(StatAddresses.MP, out mp);
            success &= connector.Read32LE(StatAddresses.MaxMP, out maxMp);
            success &= connector.Read8(StatAddresses.Strength, out strength);
            success &= connector.Read8(StatAddresses.Magic, out magic);
            success &= connector.Read8(StatAddresses.Defense, out defense);
            success &= connector.Read8(StatAddresses.AP, out ap);

            success &= connector.Write8(StatAddresses.Level, 99);
            success &= connector.Write32LE(StatAddresses.HP, 160);
            success &= connector.Write32LE(StatAddresses.MaxHP, 160);
            success &= connector.Write32LE(StatAddresses.MP, byte.MaxValue);
            success &= connector.Write32LE(StatAddresses.MaxMP, byte.MaxValue);
            success &= connector.Write8(StatAddresses.Strength, byte.MaxValue);
            success &= connector.Write8(StatAddresses.Magic, byte.MaxValue);
            success &= connector.Write8(StatAddresses.Defense, byte.MaxValue);
            success &= connector.Write8(StatAddresses.AP, byte.MaxValue);

            success &= expertMagician.StartEffect(connector);
            success &= itemaholic.StartEffect(connector);
            success &= summonChauffeur.StartEffect(connector);

            return success;
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;

            success &= connector.Write8(StatAddresses.Level, level);
            success &= connector.Write32LE(StatAddresses.HP, hp);
            success &= connector.Write32LE(StatAddresses.MaxHP, maxHp);
            success &= connector.Write32LE(StatAddresses.MP, mp);
            success &= connector.Write32LE(StatAddresses.MaxMP, maxMp);
            success &= connector.Write8(StatAddresses.Strength, strength);
            success &= connector.Write8(StatAddresses.Magic, magic);
            success &= connector.Write8(StatAddresses.Defense, defense);
            success &= connector.Write8(StatAddresses.AP, ap);

            success &= expertMagician.StopEffect(connector);
            success &= itemaholic.StopEffect(connector);
            success &= summonChauffeur.StopEffect(connector);

            return success;
        }
    }

    // TODO: Convert this to RepeatAction
    private class ZeroSora : Option
    {
        public ZeroSora() : base("Zero Sora", "Set Sora to ZERO mode, including Stats, Items, Magic, Drives and Summons.",
            Category.Sora, SubCategory.Stats,
            EffectFunction.StartTimed, durationSeconds: 30)
        {
            amnesiacMagician = new AmnesiacMagician();
            springCleaning = new SpringCleaning();
            summonTrainer = new SummonTrainer();
        }

        private byte level;
        private uint hp;
        private uint maxHp;
        private uint mp;
        private uint maxMp;
        private byte strength;
        private byte magic;
        private byte defense;
        private byte ap;

        private readonly AmnesiacMagician amnesiacMagician;
        private readonly SpringCleaning springCleaning;
        private readonly SummonTrainer summonTrainer;

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;

            success &= connector.Read8(StatAddresses.Level, out level);
            success &= connector.Read32LE(StatAddresses.HP, out hp);
            success &= connector.Read32LE(StatAddresses.MaxHP, out maxHp);
            success &= connector.Read32LE(StatAddresses.MP, out mp);
            success &= connector.Read32LE(StatAddresses.MaxMP, out maxMp);
            success &= connector.Read8(StatAddresses.Strength, out strength);
            success &= connector.Read8(StatAddresses.Magic, out magic);
            success &= connector.Read8(StatAddresses.Defense, out defense);
            success &= connector.Read8(StatAddresses.AP, out ap);

            success &= connector.Write8(StatAddresses.Level, byte.MinValue + 1);
            success &= connector.Write32LE(StatAddresses.HP, uint.MinValue + 1);
            success &= connector.Write32LE(StatAddresses.MaxHP, uint.MinValue + 1);
            success &= connector.Write32LE(StatAddresses.MP, uint.MinValue);
            success &= connector.Write32LE(StatAddresses.MaxMP, uint.MinValue);
            success &= connector.Write8(StatAddresses.Strength, byte.MinValue);
            success &= connector.Write8(StatAddresses.Magic, byte.MinValue);
            success &= connector.Write8(StatAddresses.Defense, byte.MinValue);
            success &= connector.Write8(StatAddresses.AP, byte.MinValue);

            success &= amnesiacMagician.StartEffect(connector);
            success &= springCleaning.StartEffect(connector);
            success &= summonTrainer.StartEffect(connector);

            return success;
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            bool success = true;
            success &= connector.Write8(StatAddresses.Level, level);
            success &= connector.Write32LE(StatAddresses.HP, hp);
            success &= connector.Write32LE(StatAddresses.MaxHP, maxHp);
            success &= connector.Write32LE(StatAddresses.MP, mp);
            success &= connector.Write32LE(StatAddresses.MaxMP, maxMp);
            success &= connector.Write8(StatAddresses.Strength, strength);
            success &= connector.Write8(StatAddresses.Magic, magic);
            success &= connector.Write8(StatAddresses.Defense, defense);
            success &= connector.Write8(StatAddresses.AP, ap);

            success &= amnesiacMagician.StopEffect(connector);
            success &= springCleaning.StopEffect(connector);
            success &= summonTrainer.StopEffect(connector);

            return success;
        }
    }

    private class ProCodes : Option
    {
        public ProCodes() : base("Pro-Codes", "Set Sora to consistently lose HP, MP and Drive Gauges",
            Category.Sora, SubCategory.Stats,
            EffectFunction.RepeatAction, durationSeconds: 60, refreshInterval: 1000)
        {
        }

        private uint hp;
        private uint mp;
        private uint drive;

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;

            success &= connector.Read32LE(StatAddresses.HP, out hp);
            success &= connector.Read32LE(StatAddresses.MP, out mp);
            success &= connector.Read32LE(DriveAddresses.Drive, out drive);

            return success;
        }

        public override bool DoEffect(IPS2Connector connector)
        {
            bool success = true;

            success &= connector.Write32LE(StatAddresses.HP, hp - 1);
            success &= connector.Write32LE(StatAddresses.MP, mp - 1);

            // Limit this to drop only 6 at most
            if (hp % 10 == 0)
            {
                success &= connector.Write32LE(DriveAddresses.Drive, drive - 1);
            }

            return success;
        }
    }

    private class EZCodes : Option
    {
        public EZCodes() : base("EZ-Codes", "Set Sora to consistently gain HP, MP and Drive Gauges",
            Category.Sora, SubCategory.Stats,
            EffectFunction.RepeatAction, durationSeconds: 60, refreshInterval: 1000)
        {
        }

        private uint hp;
        private uint mp;
        private uint drive;

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;

            success &= connector.Read32LE(StatAddresses.HP, out hp);
            success &= connector.Read32LE(StatAddresses.MP, out mp);
            success &= connector.Read32LE(DriveAddresses.Drive, out drive);

            return success;
        }

        public override bool DoEffect(IPS2Connector connector)
        {
            bool success = true;

            success &= connector.Write32LE(StatAddresses.HP, hp + 1);
            success &= connector.Write32LE(StatAddresses.MP, mp + 1);

            // Limit this to gain only 6 at most
            if (hp % 10 == 0)
            {
                success &= connector.Write32LE(DriveAddresses.Drive, drive + 1);
            }

            return success;
        }
    }
    #endregion
}
