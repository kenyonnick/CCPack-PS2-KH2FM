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

            connector.Write8((ulong)DriveAddresses.ButtonPress, (byte)ConstantValues.Triangle);
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
            ConstantValues.KH1Sora,
            ConstantValues.CardSora,
            ConstantValues.DieSora,
            ConstantValues.LionSora,
            ConstantValues.ChristmasSora,
            ConstantValues.SpaceParanoidsSora,
            ConstantValues.TimelessRiverSora,
            ConstantValues.Roxas,
            ConstantValues.DualwieldRoxas,
            ConstantValues.MickeyRobed,
            ConstantValues.Mickey,
            ConstantValues.Minnie,
            ConstantValues.Donald,
            ConstantValues.Goofy,
            ConstantValues.BirdDonald,
            ConstantValues.TortoiseGoofy,
            // ConstantValues.HalloweenDonald, ConstantValues.HalloweenGoofy, - Causes crash?
            // ConstantValues.ChristmasDonald, ConstantValues.ChristmasGoofy,
            ConstantValues.SpaceParanoidsDonald,
            ConstantValues.SpaceParanoidsGoofy,
            ConstantValues.TimelessRiverDonald,
            ConstantValues.TimelessRiverGoofy,
            ConstantValues.Beast,
            ConstantValues.Mulan,
            ConstantValues.Ping,
            ConstantValues.Hercules,
            ConstantValues.Auron,
            ConstantValues.Aladdin,
            ConstantValues.JackSparrow,
            ConstantValues.HalloweenJack,
            ConstantValues.ChristmasJack,
            ConstantValues.Simba,
            ConstantValues.Tron,
            ConstantValues.ValorFormSora,
            ConstantValues.WisdomFormSora,
            ConstantValues.LimitFormSora,
            ConstantValues.MasterFormSora,
            ConstantValues.FinalFormSora,
            ConstantValues.AntiFormSora
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
            success &= connector.Write16LE(CharacterAddresses.Sora, (ushort)ConstantValues.Sora);
            success &= connector.Write16LE(CharacterAddresses.LionSora, (ushort)ConstantValues.LionSora);
            success &= connector.Write16LE(CharacterAddresses.ChristmasSora, (ushort)ConstantValues.ChristmasSora);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsSora, (ushort)ConstantValues.SpaceParanoidsSora);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverSora, (ushort)ConstantValues.TimelessRiverSora);

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
            ConstantValues.ReactionValor,
            ConstantValues.ReactionWisdom,
            ConstantValues.ReactionLimit,
            ConstantValues.ReactionMaster,
            ConstantValues.ReactionFinal //ConstantValues.ReactionAnti
        ];

        public override bool StartEffect(IPS2Connector connector)
        {
            bool success = true;
            // Get us out of a Drive first if we are in one
            success &= connector.WriteFloat(DriveAddresses.DriveTime, ConstantValues.None);

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

            success &= connector.Write16LE(DriveAddresses.ReactionPopup, (ushort)ConstantValues.None);
            success &= connector.Write16LE(DriveAddresses.ReactionOption, (ushort)values[randomIndex]);
            success &= connector.Write16LE(DriveAddresses.ReactionEnable, (ushort)ConstantValues.None);

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
            ConstantValues.Minnie,
            ConstantValues.Donald,
            ConstantValues.Goofy,
            ConstantValues.BirdDonald,
            ConstantValues.TortoiseGoofy,
            //ConstantValues.HalloweenDonald, ConstantValues.HalloweenGoofy, - Causes crash?
            //ConstantValues.ChristmasDonald, ConstantValues.ChristmasGoofy, 
            ConstantValues.SpaceParanoidsDonald,
            ConstantValues.SpaceParanoidsGoofy,
            ConstantValues.TimelessRiverDonald,
            ConstantValues.TimelessRiverGoofy,
            ConstantValues.Beast,
            ConstantValues.Mulan,
            ConstantValues.Ping,
            ConstantValues.Hercules,
            ConstantValues.Auron,
            ConstantValues.Aladdin,
            ConstantValues.JackSparrow,
            ConstantValues.HalloweenJack,
            ConstantValues.ChristmasJack,
            ConstantValues.Simba,
            ConstantValues.Tron,
            ConstantValues.Riku,
            ConstantValues.AxelFriend,
            ConstantValues.LeonFriend,
            ConstantValues.YuffieFriend,
            ConstantValues.TifaFriend,
            ConstantValues.CloudFriend
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
            success &= connector.Write16LE(CharacterAddresses.Donald, ConstantValues.Donald);
            success &= connector.Write16LE(CharacterAddresses.BirdDonald, ConstantValues.BirdDonald);
            success &= connector.Write16LE(CharacterAddresses.ChristmasDonald, ConstantValues.ChristmasDonald);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsDonald, ConstantValues.SpaceParanoidsDonald);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverDonald, ConstantValues.TimelessRiverDonald);


            success &= connector.Write16LE(CharacterAddresses.Goofy, ConstantValues.Goofy);
            success &= connector.Write16LE(CharacterAddresses.TortoiseGoofy, ConstantValues.TortoiseGoofy);
            success &= connector.Write16LE(CharacterAddresses.ChristmasGoofy, ConstantValues.ChristmasGoofy);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsGoofy, ConstantValues.SpaceParanoidsGoofy);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverGoofy, ConstantValues.TimelessRiverGoofy);

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
                && connector.Write32LE(StatAddresses.Speed, ConstantValues.SlowDownx2);
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
                && connector.Write32LE(StatAddresses.Speed, ConstantValues.SpeedUpx2);
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
            return connector.Write32LE(MiscAddresses.WeaponSize, ConstantValues.TinyWeapon);
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            return connector.Write32LE(MiscAddresses.WeaponSize, ConstantValues.NormalWeapon);
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
            return connector.Write32LE(MiscAddresses.WeaponSize, ConstantValues.BigWeapon);
        }

        public override bool StopEffect(IPS2Connector connector)
        {
            return connector.Write32LE(MiscAddresses.WeaponSize, ConstantValues.NormalWeapon);
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
            success &= connector.Write16LE(EquipmentAddresses.SoraWeaponSlot, ConstantValues.StruggleBat);
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
            ConstantValues.LeonEnemy,
            ConstantValues.YuffieEnemy,
            ConstantValues.TifaEnemy,
            ConstantValues.CloudEnemy,
            ConstantValues.Xemnas,
            ConstantValues.Xigbar,
            ConstantValues.Xaldin,
            ConstantValues.Vexen,
            ConstantValues.VexenAntiSora,
            ConstantValues.Lexaeus,
            ConstantValues.Zexion,
            ConstantValues.Saix,
            ConstantValues.AxelEnemy,
            ConstantValues.Demyx,
            ConstantValues.DemyxWaterClone,
            ConstantValues.Luxord,
            ConstantValues.Marluxia,
            ConstantValues.Larxene,
            ConstantValues.RoxasEnemy,
            ConstantValues.RoxasShadow,
            ConstantValues.Sephiroth,
            ConstantValues.LingeringWill
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
            success &= connector.Write16LE(CharacterAddresses.Donald, ConstantValues.Donald);
            success &= connector.Write16LE(CharacterAddresses.BirdDonald, ConstantValues.BirdDonald);
            success &= connector.Write16LE(CharacterAddresses.ChristmasDonald, ConstantValues.ChristmasDonald);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsDonald, ConstantValues.SpaceParanoidsDonald);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverDonald, ConstantValues.TimelessRiverDonald);


            success &= connector.Write16LE(CharacterAddresses.Goofy, ConstantValues.Goofy);
            success &= connector.Write16LE(CharacterAddresses.TortoiseGoofy, ConstantValues.TortoiseGoofy);
            success &= connector.Write16LE(CharacterAddresses.ChristmasGoofy, ConstantValues.ChristmasGoofy);
            success &= connector.Write16LE(CharacterAddresses.SpaceParanoidsGoofy, ConstantValues.SpaceParanoidsGoofy);
            success &= connector.Write16LE(CharacterAddresses.TimelessRiverGoofy, ConstantValues.TimelessRiverGoofy);

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
            success &= connector.WriteFloat(DriveAddresses.DriveTime, ConstantValues.None);
            Thread.Sleep(200);

            success &= connector.Write16LE(DriveAddresses.ReactionPopup, (ushort)ConstantValues.None);

            success &= connector.Write16LE(DriveAddresses.ReactionOption, (ushort)ConstantValues.ReactionAnti);

            success &= connector.Write16LE(DriveAddresses.ReactionEnable, (ushort)ConstantValues.None);

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
                { BaseItemAddresses.Potion, new Tuple<int, int>(ConstantValues.PotionQuickSlotValue, ConstantValues.Potion) }, { BaseItemAddresses.HiPotion, new Tuple<int, int>(ConstantValues.HiPotionQuickSlotValue, ConstantValues.HiPotion) },
                { BaseItemAddresses.MegaPotion, new Tuple<int, int>(ConstantValues.MegaPotionQuickSlotValue, ConstantValues.MegaPotion) }, { BaseItemAddresses.Ether, new Tuple<int, int>(ConstantValues.EtherQuickSlotValue, ConstantValues.Ether) },
                { BaseItemAddresses.MegaEther, new Tuple<int, int>(ConstantValues.MegaEtherQuickSlotValue, ConstantValues.MegaEther) }, { BaseItemAddresses.Elixir, new Tuple<int, int>(ConstantValues.ElixirQuickSlotValue, ConstantValues.Elixir) },
                { BaseItemAddresses.Megalixir, new Tuple<int, int>(ConstantValues.MegalixirQuickSlotValue, ConstantValues.Megalixir) }, { MagicAddresses.Fire, new Tuple<int, int>(ConstantValues.FireQuickSlotValue, ConstantValues.Fire) },
                { MagicAddresses.Blizzard, new Tuple<int, int>(ConstantValues.BlizzardQuickSlotValue, ConstantValues.Blizzard) }, { MagicAddresses.Thunder, new Tuple<int, int>(ConstantValues.ThunderQuickSlotValue, ConstantValues.Thunder) },
                { MagicAddresses.Cure, new Tuple<int, int>(ConstantValues.CureQuickSlotValue, ConstantValues.Cure) }, { MagicAddresses.Reflect, new Tuple<int, int>(ConstantValues.ReflectQuickSlotValue, ConstantValues.Reflect) },
                { MagicAddresses.Magnet, new Tuple<int, int>(ConstantValues.MagnetQuickSlotValue, ConstantValues.Magnet) }
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
                return (ConstantValues.FireQuickSlotValue, success);
            if (key == MagicAddresses.Blizzard)
                return (ConstantValues.BlizzardQuickSlotValue, success);
            if (key == MagicAddresses.Thunder)
                return (ConstantValues.ThunderQuickSlotValue, success);
            if (key == MagicAddresses.Cure)
                return (ConstantValues.CureQuickSlotValue, success);
            if (key == MagicAddresses.Reflect)
                return (ConstantValues.ReflectQuickSlotValue, success);
            if (key == MagicAddresses.Magnet)
                return (ConstantValues.MagnetQuickSlotValue, success);

            return (ConstantValues.None, success);
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

                success &= connector.Write8(startAddress, (byte)ConstantValues.HighJumpMax);
                success &= connector.Write8(startAddress + 1, 0x80);

                success &= connector.Write8(startAddress + 2, (byte)ConstantValues.QuickRunMax);
                success &= connector.Write8(startAddress + 3, 0x80);

                success &= connector.Write8(startAddress + 4, (byte)ConstantValues.DodgeRollMax);
                success &= connector.Write8(startAddress + 5, 0x82);

                success &= connector.Write8(startAddress + 6, (byte)ConstantValues.AerialDodgeMax);
                success &= connector.Write8(startAddress + 7, 0x80);

                success &= connector.Write8(startAddress + 8, (byte)ConstantValues.GlideMax);
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
            success &= connector.Write8((ulong)MagicAddresses.Fire, (byte)ConstantValues.Firaga);
            success &= connector.Write8((ulong)MagicAddresses.Blizzard, (byte)ConstantValues.Blizzaga);
            success &= connector.Write8((ulong)MagicAddresses.Thunder, (byte)ConstantValues.Thundaga);
            success &= connector.Write8((ulong)MagicAddresses.Cure, (byte)ConstantValues.Curaga);
            success &= connector.Write8((ulong)MagicAddresses.Reflect, (byte)ConstantValues.Reflega);
            success &= connector.Write8((ulong)MagicAddresses.Magnet, (byte)ConstantValues.Magnega);

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

            success &= connector.Write8((ulong)MagicAddresses.Fire, (byte)ConstantValues.None);
            success &= connector.Write8((ulong)MagicAddresses.Blizzard, (byte)ConstantValues.None);
            success &= connector.Write8((ulong)MagicAddresses.Thunder, (byte)ConstantValues.None);
            success &= connector.Write8((ulong)MagicAddresses.Cure, (byte)ConstantValues.None);
            success &= connector.Write8((ulong)MagicAddresses.Reflect, (byte)ConstantValues.None);
            success &= connector.Write8((ulong)MagicAddresses.Magnet, (byte)ConstantValues.None);

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

public static class ConstantValues
{
    public static int None = 0x0;

    #region Keyblades
    public static int KingdomKey = 0x29;
    public static int Oathkeeper = 0x2A;
    public static int Oblivion = 0x2B;
    public static int DetectionSaber = 0x2C;
    public static int FrontierOfUltima = 0x2D;
    public static int StarSeeker = 0x1E0;
    public static int HiddenDragon = 0x1E1;
    public static int HerosCrest = 0x1E4;
    public static int Monochrome = 0x1E5;
    public static int FollowTheWind = 0x1E6;
    public static int CircleOfLife = 0x1E7;
    public static int PhotonDebugger = 0x1E8;
    public static int GullWing = 0x1E9;
    public static int RumblingRose = 0x1EA;
    public static int GuardianSoul = 0x1EB;
    public static int WishingLamp = 0x1EC;
    public static int DecisivePumpkin = 0x1ED;
    public static int SleepingLion = 0x1EE;
    public static int SweetMemories = 0x1EF;
    public static int MysteriousAbyss = 0x1F0;
    public static int FatalCrest = 0x1F1;
    public static int BondOfFlame = 0x1F2;
    public static int Fenrir = 0x1F3;
    public static int UltimaWeapon = 0x1F4;
    public static int TwoBecomeOne = 0x220;
    public static int WinnersProof = 0x221;
    public static ushort StruggleBat = 0x180;
    #endregion Keyblades

    #region Staffs
    public static int MagesStaff = 0x4B;
    public static int HammerStaff = 0x94;
    public static int VictoryBell = 0x95;
    public static int MeteorStaff = 0x96;
    public static int CometStaff = 0x97;
    public static int LordsBroom = 0x98;
    public static int WisdomWand = 0x99;
    public static int RisingDragon = 0x9A;
    public static int NobodyLance = 0x9B;
    public static int ShamansRelic = 0x9C;
    public static int ShamansRelicPlus = 0x258;
    public static int StaffOfDetection = 0xA1;
    public static int SaveTheQueen = 0x1E2;
    public static int SaveTheQueenPlus = 0x1F7;
    public static int Centurion = 0x221;
    public static int CenturionPlus = 0x222;
    public static int PlainMushroom = 0x223;
    public static int PlainMushroomPlus = 0x224;
    public static int PreciousMushroom = 0x225;
    public static int PreciousMushroomPlus = 0x226;
    public static int PremiumMushroom = 0227;
    #endregion Staffs

    #region Shields
    public static int KnightsShield = 0x31;
    public static int AdamantShield = 0x8B;
    public static int ChainGear = 0x8C;
    public static int OgreShield = 0x8D;
    public static int FallingStar = 0x8E;
    public static int Dreamcloud = 0x8F;
    public static int KnightDefender = 0x90;
    public static int GenjiShield = 0x91;
    public static int AkashicRecord = 0x92;
    public static int AkashicRecordPlus = 0x259;
    public static int NobodyGuard = 0x93;
    public static int DetectionShield = 0x32;
    public static int SaveTheKing = 0x1E3;
    public static int SaveTheKingPlus = 0x1F8;
    public static int FrozenPride = 0x228;
    public static int FrozenPridePlus = 0x229;
    public static int JoyousMushroom = 0x22A;
    public static int JoyousMushroomPlus = 0x22B;
    public static int MajesticMushroom = 0x22C;
    public static int MajesticMushroomPlus = 0x22D;
    public static int UltimateMushroom = 0x22E;
    #endregion Shields

    #region Equipment

    #region Armor
    public static int ElvenBandana = 0x43;
    public static int DivineBandana = 0x44;
    public static int PowerBand = 0x45;
    public static int BusterBand = 0x46;
    public static int ChampionBelt = 0x131;
    public static int ProtectBelt = 0x4E;
    public static int GaiaBelt = 0x4F;
    public static int CosmicBelt = 0x6F;
    public static int FireBangle = 0xAD;
    public static int FiraBangle = 0xAE;
    public static int FiragaBangle = 0xC5;
    public static int FiragunBangle = 0x11C;
    public static int BlizzardArmlet = 0x11E;
    public static int BlizzaraArmlet = 0x11F;
    public static int BlizzaragaArmlet = 0x120;
    public static int BlizzaragunArmlet = 0x121;
    public static int ThunderTrinket = 0x123;
    public static int ThundaraTrinket = 0x124;
    public static int ThundagaTrinket = 0x125;
    public static int ThundagunTrinket = 0x126;
    public static int ShadowAnklet = 0x127;
    public static int DarkAnklet = 0x128;
    public static int MidnightAnklet = 0x129;
    public static int ChaosAnklet = 0x12A;
    public static int AbasChain = 0x12C;
    public static int AegisChain = 0x12D;
    public static int Acrisius = 0x12E;
    public static int AcrisiusPlus = 0x133;
    public static int CosmicChain = 0x134;
    public static int ShockCharm = 0x84;
    public static int ShockCharmPlus = 0x85;
    public static int PetiteRibbon = 0x132;
    public static int Ribbon = 0x130;
    public static int GrandRibbon = 0x9D;
    #endregion Armora

    #region Accessory
    public static int AbilityRing = 0x8;
    public static int EngineersRing = 0x9;
    public static int TechiniciansRing = 0xA;
    public static int ExpertsRing = 0xB;
    public static int MastersRing = 0x22;
    public static int ExecutivesRing = 0x257;
    public static int SkillRing = 0x26;
    public static int SkillfulRing = 0x27;
    public static int CosmicRing = 0x34;
    public static int SardonyxRing = 0xC;
    public static int TourmalineRing = 0xD;
    public static int AquamarineRing = 0xE;
    public static int GarnetRing = 0xF;
    public static int DiamondRing = 0x10;
    public static int SilverRing = 0x11;
    public static int GoldRing = 0x12;
    public static int PlatinumRing = 0x13;
    public static int MythrilRing = 0x14;
    public static int OrichalcumRing = 0x1C;
    public static int SoldierEarring = 0x28;
    public static int FencerEarring = 0x2E;
    public static int MageEarring = 0x2F;
    public static int SlayerEarring = 0x30;
    public static int Medal = 0x53;
    public static int MoonAmulet = 0x23;
    public static int StarCharm = 0x24;
    public static int CosmicArts = 0x56;
    public static int ShadowArchive = 0x57;
    public static int ShadowArchivePlus = 0x58;
    public static int FullBloom = 0x40;
    public static int FullBloomPlus = 0x42;
    public static int DrawRing = 0x41;
    public static int LuckyRing = 0x3F;
    #endregion Accessory

    #region Abilities
    public static int Slapshot = 0x6;
    public static int DodgeSlash = 0x7;
    public static int SlideDash = 0x8;
    public static int GuardBreak = 0x9;
    public static int Explosion = 0xA;
    public static int FinishingLeap = 0xB;
    public static int Counterguard = 0xC;
    public static int AerialSweep = 0xD;
    public static int AerialSpiral = 0xE;
    public static int HorizontalSlash = 0xF;
    public static int AerialFinish = 0x10;
    public static int RetaliatingSlash = 0x11;
    public static int ComboMaster = 0x1B;
    public static int DamageControl = 0x1E;
    public static int FlashStep = 0x2F;
    public static int AerialDive = 0x30;
    public static int MagnetBurst = 0x31;
    public static int VicinityBreak = 0x32;
    public static int DodgeRollLv1 = 0x34;
    public static int DodgeRollLv2 = 0x35;
    public static int DodgeRollLv3 = 0x36;
    public static int DodgeRollMax = 0x37;
    public static int AutoLimit = 0x38;
    public static int Guard = 0x52;
    public static int HighJumpLv1 = 0x5E;
    public static int HighJumpLv2 = 0x5F;
    public static int HighJumpLv3 = 0x60;
    public static int HighJumpMax = 0x61;
    public static int QuickRunLv1 = 0x62;
    public static int QuickRunLv2 = 0x63;
    public static int QuickRunLv3 = 0x64;
    public static int QuickRunMax = 0x65;
    public static int AerialDodgeLv1 = 0x66;
    public static int AerialDodgeLv2 = 0x67;
    public static int AerialDodgeLv3 = 0x68;
    public static int AerialDodgeMax = 0x69;
    public static int GlideLv1 = 0x6A;
    public static int GlideLv2 = 0x6B;
    public static int GlideLv3 = 0x6C;
    public static int GlideMax = 0x6D;
    public static int AutoValor = 0x81; // TODO Find out why the toggle value is different
    public static int SecondChance = 0x81; // TODO Find out why the toggle value is different
    public static int AutoWisdom = 0x82;
    public static int AutoMaster = 0x83;
    public static int AutoFinal = 0x84;
    public static int AutoSummon = 0x85;
    public static int ComboBoost = 0x86;
    public static int AirComboBoost = 0x87;
    public static int ReactionBoost = 0x88;
    public static int FinishingPlus = 0x89; // TODO Find out why the toggle value is different
    public static int UpperSlash = 0x89; // TODO Find out why the toggle value is different
    public static int NegativeCombo = 0x8A; // TODO Find out why the toggle value is different
    public static int Scan = 0x8A; // TODO Find out why the toggle value is different
    public static int BerserkCharge = 0x8B;
    public static int DamageDrive = 0x8C;
    public static int DriveBoost = 0x8D;
    public static int FormBoost = 0x8E;
    public static int SummonBoost = 0x8F;
    public static int CombinationBoost = 0x90;
    public static int ExperienceBoost = 0x91;
    public static int LeafBracer = 0x92;
    public static int MagicLockOn = 0x93;
    public static int NoExperience = 0x94;
    public static int Draw = 0x95;
    public static int Jackpot = 0x96;
    public static int LuckyLucky = 0x97;
    public static int DriveConverter = 0x98;
    public static int FireBoost = 0x98;
    public static int BlizzardBoost = 0x99;
    public static int ThunderBoost = 0x9A;
    public static int ItemBoost = 0x9B;
    public static int MPRage = 0x9C;
    public static int MPHaste = 0x9D;
    public static int MPHastega = 0x9E; // TODO Find out why the toggle value is different
    public static int AerialRecovery = 0x9E; // TODO Find out why the toggle value is different
    public static int Defender = 0x9E; // TODO Find out why the toggle value is different
    public static int OnceMore = 0xA0;
    public static int ComboPlus = 0xA2; // TODO Find out why the toggle value is different
    public static int AutoChange = 0xA2; // TODO Find out why the toggle value is different
    public static int AirComboPlus = 0xA3; // TODO Find out why the toggle value is different
    public static int HyperHealing = 0xA3; // TODO Find out why the toggle value is different
    public static int AutoHealing = 0xA4;
    public static int MPHastera = 0xA5; // TODO Find out why the toggle value is different
    public static int DonaldFire = 0xA5; // TODO Find out why the toggle value is different
    public static int DonaldBlizzard = 0xA6;
    public static int DonaldThunder = 0xA7; // TODO Find out why the toggle value is different
    public static int GoofyTornado = 0xA7; // TODO Find out why the toggle value is different
    public static int DonaldCure = 0xA8;
    public static int GoofyTurbo = 0xA9;
    public static int SlashFrenzy = 0xAA;
    public static int Quickplay = 0xAB;
    public static int Divider = 0xAC;
    public static int GoofyBash = 0xAD;
    public static int FerociousRush = 0xAE;
    public static int BlazingFury = 0xAF;
    public static int IcyTerror = 0xB0; // TODO Find out why the toggle value is different
    public static int HealingWater = 0xB0; // TODO Find out why the toggle value is different
    public static int BoltsOfSorrow = 0xB1; // TODO Find out why the toggle value is different
    public static int FuriousShout = 0xB1; // TODO Find out why the toggle value is different
    public static int MushuFire = 0xB2;
    public static int Flametongue = 0xB3;
    public static int DarkShield = 0xB4;
    public static int Groundshaker = 0xB6; // TODO Find out why the toggle value is different
    public static int DarkAura = 0xB6; // TODO Find out why the toggle value is different
    public static int FierceClaw = 0xB7;
    public static int CurePotion = 0xBB;
    public static int ScoutingDisk = 0xBC;
    public static int HealingHerb = 0xBE; // TODO Find out why the toggle value is different
    public static int NoMercy = 0xBE; // TODO Find out why the toggle value is different
    public static int RainStorm = 0xBF;
    public static int BoneSmash = 0xC0;
    public static int TrinityLimit = 0xC6;
    public static int Fantasia = 0xC7;
    public static int FlareForce = 0xC8;
    public static int TornadoFusion = 0xC9;
    public static int TrickFantasy = 0xCB;
    public static int Overdrive = 0xCC;
    public static int HowlingMoon = 0xCD;
    public static int AplauseAplause = 0xCE;
    public static int Dragonblaze = 0xCF;
    public static int Teamwork = 0xCA;
    public static int EternalSession = 0xD0;
    public static int KingsPride = 0xD1;
    public static int TreasureIsle = 0xD2;
    public static int CompleteCompilment = 0xD3;
    public static int PulsingThunder = 0xD7;

    public static int SoraAbilityCount = 148;
    public static int DonaldAbilityCount = 34;
    public static int GoofyAbilityCount = 34;
    public static int MulanAbilityCount = 16;
    public static int BeastAbilityCount = 16;
    public static int AuronAbilityCount = 14;
    public static int CaptainJackSparrowAbilityCount = 24;
    public static int AladdinAbilityCount = 18;
    public static int JackSkellingtonAbilityCount = 22;
    public static int SimbaAbilityCount = 18;
    public static int TronAbilityCount = 18;
    public static int RikuAbilityCount = 22;
    #endregion Abilities

    #endregion Equipment

    #region Items
    public static int Potion = 0x1;
    public static int HiPotion = 0x2;
    public static int Ether = 0x3;
    public static int Elixir = 0x4;
    public static int MegaPotion = 0x5;
    public static int MegaEther = 0x6;
    public static int Megalixir = 0x7;
    #endregion Items

    #region Abilities

    #endregion Abilities

    #region Characters
    public static int Sora = 0x54;
    public static int KH1Sora = 0x6C1;
    public static int CardSora = 0x601;
    public static int DieSora = 0x602;
    public static int LionSora = 0x28A;
    public static int ChristmasSora = 0x955;
    public static int SpaceParanoidsSora = 0x656;
    public static int TimelessRiverSora = 0x955;

    public static ushort ValorFormSora = 0x55;
    public static int WisdomFormSora = 0x56;
    public static int LimitFormSora = 0x95D;
    public static int MasterFormSora = 0x57;
    public static int FinalFormSora = 0x58;
    public static int AntiFormSora = 0x59;

    public static int Roxas = 0x5A;
    public static int DualwieldRoxas = 0x323;

    public static int MickeyRobed = 0x5B;
    public static int Mickey = 0x318;

    public static ushort Donald = 0x5C;
    public static ushort BirdDonald = 0x5EF;
    public static ushort HalloweenDonald = 0x29E;
    public static ushort ChristmasDonald = 0x95B;
    public static ushort SpaceParanoidsDonald = 0x55A;
    public static ushort TimelessRiverDonald = 0x5CF;

    public static ushort Goofy = 0x5D;
    public static ushort TortoiseGoofy = 0x61B;
    public static ushort HalloweenGoofy = 0x29D;
    public static ushort ChristmasGoofy = 0x95C;
    public static ushort SpaceParanoidsGoofy = 0x554;
    public static ushort TimelessRiverGoofy = 0x4F5;

    public static int Beast = 0x5E;
    public static int Ping = 0x64;
    public static int Mulan = 0x63;
    public static int Auron = 0x65;
    public static int Aladdin = 0x62;
    public static int JackSparrow = 0x66;
    public static int HalloweenJack = 0x5F;
    public static int ChristmasJack = 0x60;
    public static int Simba = 0x61;
    public static int Tron = 0x2D4;
    public static int Hercules = 0x16A;
    public static int Minnie = 0x4BB;
    public static int Riku = 0x819;

    public static int AxelFriend = 0x4DC;
    public static int LeonFriend = 0x61C;
    public static int YuffieFriend = 0x6B0;
    public static int TifaFriend = 0x6B3;
    public static int CloudFriend = 0x688;

    public static int LeonEnemy = 0x8F8;
    public static int YuffieEnemy = 0x8FB;
    public static int TifaEnemy = 0x8FA;
    public static int CloudEnemy = 0x8F9;

    public static int Xemnas = 0x81F;
    public static int Xigbar = 0x622;
    public static int Xaldin = 0x3E5;
    public static int Vexen = 0x933;
    public static int VexenAntiSora = 0x934;
    public static int Lexaeus = 0x935;
    public static int Zexion = 0x97B;
    public static int Saix = 0x6C9;
    public static int AxelEnemy = 0x51;
    public static int Demyx = 0x31B;
    public static int DemyxWaterClone = 0x8F6;
    public static int Luxord = 0x5F8;
    public static int Marluxia = 0x923;
    public static int Larxene = 0x962;
    public static int RoxasEnemy = 0x951;
    public static int RoxasShadow = 0x754;

    public static int Sephiroth = 0x8B6;
    public static int LingeringWill = 0x96F;
    #endregion Characters

    #region Magic
    public static int Fire = 0x1;
    public static int Fira = 0x2;
    public static int Firaga = 0x3;
    public static int Blizzard = 0x1;
    public static int Blizzara = 0x2;
    public static int Blizzaga = 0x3;
    public static int Thunder = 0x1;
    public static int Thundara = 0x2;
    public static int Thundaga = 0x3;
    public static int Cure = 0x1;
    public static int Cura = 0x2;
    public static int Curaga = 0x3;
    public static int Reflect = 0x1;
    public static int Reflera = 0x2;
    public static int Reflega = 0x3;
    public static int Magnet = 0x1;
    public static int Magnera = 0x2;
    public static int Magnega = 0x3;
    #endregion Magic

    #region Speed
    public static uint SlowDownx3 = 0x40C00000;
    public static uint SlowDownx2 = 0x40500000;
    public static uint SlowDownx1 = 0x40000000;
    public static uint NormalSpeed = 0x41000000;
    public static uint SpeedUpx1 = 0x41C00000;
    public static uint SpeedUpx2 = 0x42500000;
    public static uint SpeedUpx3 = 0x42E00000;
    #endregion Speed

    #region Invulnerability
    //public static int Invulnerability_1 = 0x8C820004;
    public static int Invulnerability_2 = 0x0806891E;
    //public static int Invulnerability_3 = 0xAC820000;
    public static int Invulnerability_4 = 0x0C03F800;

    public static int InvulnerabilityFalse = 0x30E7FFFF;
    #endregion Invulnerability

    #region Quick Slot Values
    public static int PotionQuickSlotValue = 0x17;
    public static int HiPotionQuickSlotValue = 0x14;
    public static int MegaPotionQuickSlotValue = 0xF2;
    public static int EtherQuickSlotValue = 0x15;
    public static int MegaEtherQuickSlotValue = 0xF3;
    public static int ElixirQuickSlotValue = 0xF4;
    public static int MegalixirQuickSlotValue = 0xF4;
    public static int FireQuickSlotValue = 0x31;
    public static int BlizzardQuickSlotValue = 0x33;
    public static int ThunderQuickSlotValue = 0x32;
    public static int CureQuickSlotValue = 0x34;
    public static int MagnetQuickSlotValue = 0xAE;
    public static int ReflectQuickSlotValue = 0xB1;
    #endregion Quick Slot Values

    public static byte Triangle = 0xEF;
    public static uint TinyWeapon = 0x3F000000;
    public static uint NormalWeapon = 0x3F800000;
    public static uint BigWeapon = 0xC1000000;

    public static uint ReactionValor = 0x6;
    public static uint ReactionWisdom = 0x7;
    public static uint ReactionLimit = 0x2A2;
    public static uint ReactionMaster = 0xB;
    public static uint ReactionFinal = 0xC;
    public static uint ReactionAnti = 0xD;
}
