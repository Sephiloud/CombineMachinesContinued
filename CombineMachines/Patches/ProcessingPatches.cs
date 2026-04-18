using CombineMachines.Helpers;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Extensions;
using StardewValley.GameData.Machines;
using StardewValley.Inventories;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using System;
using System.Collections.Generic;
using System.Linq;
using SObject = StardewValley.Object;
using System.Reflection;

namespace CombineMachines.Patches
{
    public static class ProcessingPatches
    {
        private const string ModDataExecutingFunctionKey = "CombineMachines_ExecutingFunction";

        internal static void Entry(IModHelper helper, Harmony Harmony)
        {
            //  StardewValley.Object.OutputMachine is generally when the machine's heldObject and its minutesUntilReady are set
            //  So whenever this function is invoked, apply a postfix patch that will recalculate the heldObject.Stack or the minutesUntilReady, depending on the combined processing power of the machine
            Harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.OutputMachine)),
                prefix: new HarmonyMethod(typeof(OutputMachinePatch), nameof(OutputMachinePatch.Prefix)),
                postfix: new HarmonyMethod(typeof(OutputMachinePatch), nameof(OutputMachinePatch.Postfix))
            );

            // The logic to handle lightning rods. Reimplements the whole vanilla function with combin features added as prefix and stops propagation.
            Harmony.Patch(
                original: AccessTools.Method(typeof(Utility), nameof(Utility.performLightningUpdate)),
                prefix: new HarmonyMethod(typeof(PerformLightningUpdatePatch), nameof(PerformLightningUpdatePatch.Prefix))
            );

            //  The logic we need in StardewValley.Object.OutputMachine's Postfix depends on which function is calling it. 
            //  If it's invoked from StardewValley.Object.PlaceInMachine, we must cap the processing power based on how many of the required inputs the player has.
            //  For example, if ProcessingPower=900% and player has 40 copper ore to insert into a furnace, the max multiplier is x8 instead of x9.
            //  The following patches just keep track of which calling function is executing when OutputMachine is invoked so that our postfix can implement different logic based on where its being called from
            Harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.performDropDownAction)),
                prefix: new HarmonyMethod(typeof(PerformDropDownActionPatch), nameof(PerformDropDownActionPatch.Prefix)),
                postfix: new HarmonyMethod(typeof(PerformDropDownActionPatch), nameof(PerformDropDownActionPatch.Postfix))
            );
            Harmony.Patch(
                original: AccessTools.Method(typeof(SObject), "CheckForActionOnMachine" /*nameof(SObject.CheckForActionOnMachine)*/),
                prefix: new HarmonyMethod(typeof(CheckForActionOnMachinePatch), nameof(CheckForActionOnMachinePatch.Prefix)),
                postfix: new HarmonyMethod(typeof(CheckForActionOnMachinePatch), nameof(CheckForActionOnMachinePatch.Postfix))
            );
            Harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.PlaceInMachine)),
                prefix: new HarmonyMethod(typeof(PlaceInMachinePatch), nameof(PlaceInMachinePatch.Prefix)),
                postfix: new HarmonyMethod(typeof(PlaceInMachinePatch), nameof(PlaceInMachinePatch.Postfix))
            );
            Harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.DayUpdate)),
                prefix: new HarmonyMethod(typeof(DayUpdatePatch), nameof(DayUpdatePatch.Prefix)),
                postfix: new HarmonyMethod(typeof(DayUpdatePatch), nameof(DayUpdatePatch.Postfix))
            );

            //  Tappers don't get their outputs from StardewValley.Object.OutputMachine
            //  so we need to patch StardewValley.TerrainFeatures.Tree.UpdateTapperProduct
            Harmony.Patch(
                original: AccessTools.Method(typeof(Tree), nameof(Tree.UpdateTapperProduct)),
                //prefix: new HarmonyMethod(typeof(UpdateTapperProductPatch), nameof(UpdateTapperProductPatch.Prefix)),
                postfix: new HarmonyMethod(typeof(UpdateTapperProductPatch), nameof(UpdateTapperProductPatch.Postfix))
            );

            // Junimatic Compatibility Patch
            if (helper.ModRegistry.IsLoaded("NermNermNerm.Junimatic"))
            {
                ModEntry.Logger.Log("Mod NermNermNerm.Junimatic was detected - Combine Machines Continued Junimatic Compatibility Patch is applied.", ModEntry.InfoLogLevel);
                var type = AccessTools.TypeByName("NermNermNerm.Junimatic.ObjectMachine");
                var origin = AccessTools.Method(type, "GetRecipeFromChest");
                Harmony.Patch(
                    original: origin,
                    postfix: new HarmonyMethod(typeof(JunimaticCompatibilityPatch), nameof(JunimaticCompatibilityPatch.Postfix)) 
                        { priority = HarmonyLib.Priority.Last }
                );
            }
        }

        [HarmonyPatch(typeof(Tree), nameof(Tree.UpdateTapperProduct))]
        public static class UpdateTapperProductPatch
        {
            //public static bool Prefix(Tree __instance, SObject tapper, SObject previousOutput = null, bool onlyPerformRemovals = false)
            //{
            //    if (Game1.IsMasterGame)
            //    {
            //        ModEntry.Logger.Log($"{nameof(UpdateTapperProductPatch)}.{nameof(Prefix)}: {tapper.DisplayName} ({tapper.TileLocation})", ModEntry.InfoLogLevel);
            //        //__instance.modData[ModDataExecutingFunctionKey] = nameof(Tree.UpdateTapperProduct);
            //    }
            //    return true;
            //}

            public static void Postfix(Tree __instance, SObject tapper, SObject previousOutput = null, bool onlyPerformRemovals = false)
            {
                if (!Context.IsWorldReady)
                    return; // I guess UpdateTapperProduct is invoked while initially loading the game, which could cause tapper products to multiply on each save load

                if (tapper.TryGetCombinedQuantity(out int CombinedQty) && CombinedQty > 1 && tapper.heldObject.Value != null) // && Game1.IsMasterGame // not necessary, still called once by the game placing or collecting it
                {
                    //ModEntry.Logger.Log($"{nameof(UpdateTapperProductPatch)}.{nameof(Postfix)}: {tapper.DisplayName} ({tapper.TileLocation})", ModEntry.InfoLogLevel);
                    //_ = __instance.modData.Remove(ModDataExecutingFunctionKey);

                    const string ModDataKey = "CombineMachines_HasModifiedTapperOutput"; // After modifying the tapper product, set a flag to true so we don't accidentally keep modifying it and stack the effects
                    if (!tapper.heldObject.Value.modData.TryGetValue(ModDataKey, out string IsModifiedString) || !bool.TryParse(IsModifiedString, out bool IsModified) || !IsModified)
                    {
                        if (OutputMachinePatch.TryUpdateMinutesUntilReady(tapper, CombinedQty, out int PreviousMinutes, out int NewMinutes, out double DurationMultiplier))
                        {
                            ModEntry.Logger.Log($"{nameof(UpdateTapperProductPatch)}.{nameof(Postfix)}: " +
                                $"Set {tapper.DisplayName} MinutesUntilReady from {PreviousMinutes} to {NewMinutes} " +
                                $"({(DurationMultiplier * 100.0).ToString("0.##")}%, " +
                                $"Target value before weighted rounding = {(DurationMultiplier * PreviousMinutes).ToString("0.#")})", ModEntry.InfoLogLevel);

                            tapper.heldObject.Value.modData[ModDataKey] = "true";
                        }

                        if (ModEntry.UserConfig.ShouldModifyInputsAndOutputs(tapper))
                        {
                            double ProcessingPower = ModEntry.UserConfig.ComputeProcessingPower(CombinedQty);

                            int PreviousOutputStack = tapper.heldObject.Value.Stack;

                            double DesiredNewValue = PreviousOutputStack * Math.Max(1.0, ProcessingPower);
                            int NewOutputStack = RNGHelpers.WeightedRound(DesiredNewValue);

                            tapper.heldObject.Value.Stack = NewOutputStack;
                            ModEntry.LogTrace(CombinedQty, tapper, tapper.TileLocation, "HeldObject.Stack", PreviousOutputStack, DesiredNewValue, NewOutputStack, ProcessingPower);

                            tapper.heldObject.Value.modData[ModDataKey] = "true";
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(SObject), nameof(SObject.performDropDownAction))]
        public static class PerformDropDownActionPatch
        {
            public static bool Prefix(SObject __instance, Farmer who)
            {
                if (true)//Game1.IsMasterGame)
                {
                    //ModEntry.Logger.Log($"{nameof(PerformDropDownActionPatch)}.{nameof(Prefix)}: {__instance.DisplayName} ({__instance.TileLocation})", ModEntry.InfoLogLevel);
                    __instance.modData[ModDataExecutingFunctionKey] = nameof(SObject.performDropDownAction);
                }
                return true;
            }

            public static void Postfix(SObject __instance, Farmer who)
            {
                if (true)//Game1.IsMasterGame)
                {
                    //ModEntry.Logger.Log($"{nameof(PerformDropDownActionPatch)}.{nameof(Postfix)}: {__instance.DisplayName} ({__instance.TileLocation})", ModEntry.InfoLogLevel);
                    _ = __instance.modData.Remove(ModDataExecutingFunctionKey);
                }
            }
        }

        [HarmonyPatch(typeof(SObject), "CheckForActionOnMachine" /*nameof(SObject.CheckForActionOnMachine)*/)]
        public static class CheckForActionOnMachinePatch
        {
            public static bool Prefix(SObject __instance, Farmer who, bool justCheckingForActivity = false)
            {
                if (!justCheckingForActivity)
                {
                    //ModEntry.Logger.Log($"{nameof(CheckForActionOnMachinePatch)}.{nameof(Prefix)}: {__instance.DisplayName} ({__instance.TileLocation})", ModEntry.InfoLogLevel);
                    __instance.modData[ModDataExecutingFunctionKey] = "CheckForActionOnMachine" /*nameof(SObject.CheckForActionOnMachine)*/;
                }
                return true;
            }

            public static void Postfix(SObject __instance, Farmer who, bool justCheckingForActivity = false)
            {
                if (!justCheckingForActivity)
                {
                    //ModEntry.Logger.Log($"{nameof(CheckForActionOnMachinePatch)}.{nameof(Postfix)}: {__instance.DisplayName} ({__instance.TileLocation})", ModEntry.InfoLogLevel);
                    _ = __instance.modData.Remove(ModDataExecutingFunctionKey);
                }
            }
        }

        [HarmonyPatch(typeof(SObject), nameof(SObject.PlaceInMachine))]
        public static class PlaceInMachinePatch
        {
            public static bool Prefix(SObject __instance, MachineData machineData, Item inputItem, bool probe, Farmer who, bool showMessages = true, bool playSounds = true)
            {
                if (!probe)
                {
                    //ModEntry.Logger.Log($"{nameof(PlaceInMachinePatch)}.{nameof(Prefix)}: {__instance.DisplayName} ({__instance.TileLocation})", ModEntry.InfoLogLevel);
                    __instance.modData[ModDataExecutingFunctionKey] = nameof(SObject.PlaceInMachine);
                }
                return true;
            }

            public static void Postfix(SObject __instance, MachineData machineData, Item inputItem, bool probe, Farmer who, bool showMessages = true, bool playSounds = true)
            {
                if (!probe)
                {
                    //ModEntry.Logger.Log($"{nameof(PlaceInMachinePatch)}.{nameof(Postfix)}: {__instance.DisplayName} ({__instance.TileLocation})", ModEntry.InfoLogLevel);
                    _ = __instance.modData.Remove(ModDataExecutingFunctionKey);
                }
            }
        }

        [HarmonyPatch(typeof(SObject), nameof(SObject.DayUpdate))]
        public static class DayUpdatePatch
        {
            public static bool Prefix(SObject __instance)
            {
                if (__instance.IsCombinedMachine())
                {
                    //ModEntry.Logger.Log($"{nameof(DayUpdatePatch)}.{nameof(Prefix)}: {__instance.DisplayName} ({__instance.TileLocation})", ModEntry.InfoLogLevel);
                    __instance.modData[ModDataExecutingFunctionKey] = nameof(SObject.DayUpdate);
                }
                return true;
            }

            public static void Postfix(SObject __instance)
            {
                if (__instance.IsCombinedMachine())
                {
                    //ModEntry.Logger.Log($"{nameof(DayUpdatePatch)}.{nameof(Postfix)}: {__instance.DisplayName} ({__instance.TileLocation})", ModEntry.InfoLogLevel);
                    _ = __instance.modData.Remove(ModDataExecutingFunctionKey);
                }
            }
        }

        [HarmonyPatch(typeof(SObject), nameof(SObject.OutputMachine))]
        public static class OutputMachinePatch
        {
            private static readonly IReadOnlyList<string> HandledCallerFunctions = new List<string>()
            { 
                nameof(SObject.performDropDownAction),
                nameof(SObject.DayUpdate),
                nameof(SObject.PlaceInMachine),
                "CheckForActionOnMachine", // nameof(SObject.CheckForActionOnMachine)
                "CalledByCodeMod", // No Caller set = it was probably called by a function in a mod, like Automate!
                "CheckForActionOnMachineWithInputItem", // Caller mods can use to use the Crystalarium behaviour
            };
            private static readonly string CoalQualifiedId = "(O)382";
            private static readonly string CrystalariumQualifiedId = "(BC)21";

            public static bool Prefix(SObject __instance, MachineData machine, MachineOutputRule outputRule, Item inputItem, Farmer who, GameLocation location, bool probe)
            {
                try
                {
                    if (!probe && __instance.TryGetCombinedQuantity(out int CombinedQty))
                    {
                        //ModEntry.Logger.Log($"{nameof(OutputMachinePatch)}.{nameof(Prefix)}: {__instance.DisplayName} ({__instance.TileLocation})", ModEntry.InfoLogLevel);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    ModEntry.Logger.Log($"Unhandled Error in {nameof(OutputMachinePatch)}.{nameof(Prefix)}:\n{ex}", LogLevel.Error);
                    return true;
                }
            }

            public static void Postfix(SObject __instance, MachineData machine, MachineOutputRule outputRule, Item inputItem, Farmer who, GameLocation location, bool probe)
            {
                try
                {
                    if (probe || (!Game1.IsMasterGame && SObject.autoLoadFrom != null) || __instance.IsTapper() || !__instance.TryGetCombinedQuantity(out int CombinedQty) || CombinedQty <= 1)
                        return;

                    //ModEntry.Logger.Log($"Begin {nameof(OutputMachinePatch)}.{nameof(Postfix)}: {__instance.DisplayName} ({__instance.TileLocation})", ModEntry.InfoLogLevel);

                    SObject Machine = __instance;

                    if (!__instance.modData.TryGetValue(ModDataExecutingFunctionKey, out string CallerName))
                    {
                        ModEntry.Logger.Log($"{nameof(OutputMachinePatch)}.{nameof(Postfix)}: " +
                            $"SObject.OutputMachine was called by a non vanilla function for the machine Id: {Machine.QualifiedItemId} without setting proper compatability.\n"
                            + "Support is added based on assumptions. If something does not work with this machine, compatability changes need to be made.", ModEntry.InfoLogLevel);
                            // + "It's assumed to be called by a function implentend by a different C# Mod, without adding explicit support.\n\n"
                            // + "If no input item is provided, it is assumed to be a auto producer like a Worm Bin.\n\n"
                            // + "If an input item is provided, it is assumed to be equivalent to SObject.PlaceInMachine and "
                            // + "tries to get more items from the given inventory based on the CombinedPower.\n"
                            // + "If no autoLoadFrom Inventory is set and no Player is given, the inventory of the Host Player will be used.\n"
                            // + "If this is not intended the Mod calling OutputMachine should make compatibility changes:\n"
                            // + $"Set for the machine instance only for this call the mod data property {ModDataExecutingFunctionKey} to "
                            // + "CheckForActionOnMachine (for no input item and no consuming), "
                            // + "CheckForActionOnMachineWithInputItem (to allow an input item but not consume additional ones, like the Crystalarium does), "
                            // + "PlaceInMachine (to consume items form the given player inventory or autoLoadFrom inventory)\n\n"
                            // + "For further questions you can ask me @sephiloud", ModEntry.InfoLogLevel);
                        CallerName = "CalledByCodeMod";
                    }

                    ModEntry.Logger.Log($"{nameof(OutputMachinePatch)}.{nameof(Postfix)}: "
                            + $"SObject.OutputMachine was called by {CallerName} and from: {who?.name.ToString()} (userId: {who?.UniqueMultiplayerID}, "
                            + $"isHost: {who?.UniqueMultiplayerID == Game1.MasterPlayer.UniqueMultiplayerID}, isMasterGame: {Game1.IsMasterGame})", ModEntry.InfoLogLevel);
                    if (!Game1.IsMasterGame && (CallerName == nameof(SObject.DayUpdate) || CallerName == "CalledByCodeMod"))
                        return;

                    who ??= Game1.MasterPlayer;
                    if (TryUpdateMinutesUntilReady(Machine, CombinedQty, out int PreviousMinutes, out int NewMinutes, out double DurationMultiplier))
                    {
                        ModEntry.Logger.Log($"{nameof(OutputMachinePatch)}.{nameof(Postfix)}: " +
                            $"Set {Machine.DisplayName} MinutesUntilReady from {PreviousMinutes} to {NewMinutes} " +
                            $"({(DurationMultiplier * 100.0).ToString("0.##")}%, " +
                            $"Target value before weighted rounding = {(DurationMultiplier * PreviousMinutes).ToString("0.#")})", ModEntry.InfoLogLevel);
                        return;
                    }
                        
                    if (ModEntry.UserConfig.ShouldModifyInputsAndOutputs(Machine) && Machine.heldObject.Value != null && HandledCallerFunctions.Contains(CallerName))
                    {
                        //  Compute the output Stack multiplier
                        //  If the output item required no inputs, then the multiplier is equal to the machine's processing power.
                        //  Otherwise it might be capped depending on how many more of the input items the player has
                        double ActualProcessingPower;
                        switch (CallerName)
                        {
                            case nameof(SObject.performDropDownAction):
                            case nameof(SObject.DayUpdate):
                            case "CheckForActionOnMachine": // nameof(SObject.CheckForActionOnMachine)
                                if (inputItem != null)
                                {
                                    //  Crystalariums have an inputItem equal to whatever gem was inserted into it, but are a special-case because more inputs don't need to be consumed during this function
                                    //  (Extra inputs for Crystalariums only need to be consumed during the SObject.PlaceInMachine function)
                                    if (Machine.QualifiedItemId != CrystalariumQualifiedId)
                                        throw new Exception($"Calling {nameof(OutputMachinePatch)}.{nameof(Postfix)} from {CallerName}: Expected null input item for machine '{Machine.DisplayName}'. (Actual input item: {inputItem.DisplayName})");
                                }
                                ActualProcessingPower = ModEntry.UserConfig.ComputeProcessingPower(CombinedQty);
                                break;
                            case "CalledByCodeMod":
                                if (inputItem == null)
                                {
                                    ActualProcessingPower = ModEntry.UserConfig.ComputeProcessingPower(CombinedQty);
                                    break;
                                }
                                goto case nameof(SObject.PlaceInMachine);
                            case "CheckForActionOnMachineWithInputItem":
                                ActualProcessingPower = ModEntry.UserConfig.ComputeProcessingPower(CombinedQty);
                                break;
                            case nameof(SObject.PlaceInMachine):
                                if (inputItem == null)
                                    throw new Exception($"Calling {nameof(OutputMachinePatch)}.{nameof(Postfix)} from {CallerName}: Expected non-null input item.");

                                //  Get the trigger rule being used to generate the output item
                                if (!MachineDataUtility.TryGetMachineOutputRule(__instance, machine, MachineOutputTrigger.ItemPlacedInMachine, inputItem, who, location,
                                    out MachineOutputRule rule, out MachineOutputTriggerRule triggerRule, out MachineOutputRule ruleIgnoringCount, out MachineOutputTriggerRule triggerIgnoringCount))
                                {
                                    return;
                                }

                                IInventory Inventory = SObject.autoLoadFrom ?? who.Items;
                                double MaxMultiplier = ModEntry.UserConfig.ComputeProcessingPower(CombinedQty);
                                bool MultiplyCoalInputs = ModEntry.UserConfig.FurnaceMultiplyCoalInputs;

                                // TODO: May be incompatible with other mods calling OutputMachine directly with different base conditions than I assume here
                                // Assume CalledByMod is like Automate -> calling with autoLoadFrom == null means it is something equivalent to an automatic call like DayCheck or CheckForActionOnMachine
                                // -> CalledByCodeMod reaching this point has an input item, meaning I can mostly assume it's a machine working like the Crystalarium (one time input item, producing indefinitely)
                                if (MaxMultiplier > 1.0 && CallerName == "CalledByCodeMod" && SObject.autoLoadFrom == null)
                                {
                                    bool hasInputItemStack = Machine.modData.TryGetValue(ModEntry.ModDataInputItemStack, out string InputItemStackString);
                                    if (SObject.autoLoadFrom == null && hasInputItemStack && int.TryParse(InputItemStackString, out int InputItemStack))
                                    {
                                        ModEntry.Logger.Log($"{nameof(OutputMachinePatch)}.{nameof(Postfix)}: "
                                            + $"Modifying {Machine.DisplayName} (Id: {Machine.QualifiedItemId}) based on input item stack: {InputItemStack}!", ModEntry.InfoLogLevel);
                                        ActualProcessingPower = InputItemStack;
                                        break;
                                    }
                                }

                                //  Note: Some machines (such as Fish Smokers) don't require a specific input item, so the triggerrule's RequiredItemId would be null
                                string MainIngredientId = triggerRule.RequiredItemId ?? inputItem.QualifiedItemId;

                                //  Cap the multiplier based on how many of the main input item the player has.
                                //  EX: If inserting copper ore, a bar requires 5 ore. If player has 40 ore, the max multiplier cannot exceed 40/5=8.0
                                int MainInputQty = Inventory.CountId(MainIngredientId);
                                MaxMultiplier = Math.Min(MaxMultiplier, MainInputQty * 1.0 / triggerRule.RequiredCount);

                                //  Cap the multiplier based on how many of the secondary input item(s) the player has.
                                //  Typically this would be things like Coal for smelting bars or using the fish smoker.
                                if (machine.AdditionalConsumedItems != null)
                                {
                                    foreach (MachineItemAdditionalConsumedItems SecondaryIngredient in machine.AdditionalConsumedItems)
                                    {
                                        if (!MultiplyCoalInputs && SecondaryIngredient.ItemId == CoalQualifiedId)
                                            continue;

                                        int SecondaryInputQty = Inventory.CountId(SecondaryIngredient.ItemId);
                                        MaxMultiplier = Math.Min(MaxMultiplier, SecondaryInputQty * 1.0 / SecondaryIngredient.RequiredCount);
                                    }
                                }

                                ActualProcessingPower = MaxMultiplier;

                                //  Consume the extra inputs
                                if (ActualProcessingPower > 1.0)
                                {
                                    //  Consume main input, save item input stack used as mod data on the machine
                                    int itemInputStack = RNGHelpers.WeightedRound(ActualProcessingPower * triggerRule.RequiredCount);
                                    Machine.modData[ModEntry.ModDataInputItemStack] = itemInputStack.ToString();
                                    // -triggerRule.RequiredCount because 100% of inputs have already been consumed by vanilla game functions
                                    int extraConsumedMainIngredientCount = itemInputStack - triggerRule.RequiredCount;
                                    Inventory.ReduceId(MainIngredientId, extraConsumedMainIngredientCount); 
                                    if (Machine.QualifiedItemId == CrystalariumQualifiedId) Machine.modData[ModEntry.ModDataInputItemStack] = itemInputStack.ToString();

                                    //  Consume secondary input(s)
                                    if (machine.AdditionalConsumedItems != null)
                                    {
                                        foreach (MachineItemAdditionalConsumedItems SecondaryIngredient in machine.AdditionalConsumedItems)
                                        {
                                            if (!MultiplyCoalInputs && SecondaryIngredient.ItemId == CoalQualifiedId)
                                                continue;

                                            Inventory.ReduceId(SecondaryIngredient.ItemId, RNGHelpers.WeightedRound((ActualProcessingPower - 1.0) * SecondaryIngredient.RequiredCount));
                                        }
                                    }
                                }
                                break;
                            default:
                                throw new NotImplementedException($"Calling {nameof(OutputMachinePatch)}.{nameof(Postfix)} from {CallerName}. Expected {nameof(CallerName)} to be one of the following: {string.Join(",", HandledCallerFunctions)}");
                        }

                        int PreviousOutputStack = Machine.heldObject.Value.Stack;

                        double DesiredNewValue = PreviousOutputStack * Math.Max(1.0, ActualProcessingPower);
                        int NewOutputStack = RNGHelpers.WeightedRound(DesiredNewValue);

                        Machine.heldObject.Value.Stack = NewOutputStack;
                        ModEntry.LogTrace(CombinedQty, Machine, Machine.TileLocation, "HeldObject.Stack", PreviousOutputStack, DesiredNewValue, NewOutputStack, ActualProcessingPower);
                    }

                    //ModEntry.Logger.Log($"End {nameof(OutputMachinePatch)}.{nameof(Postfix)}: {__instance.DisplayName} ({__instance.TileLocation})", ModEntry.InfoLogLevel);
                }
                catch (Exception ex)
                {
                    ModEntry.Logger.Log($"Unhandled Error in {nameof(MinutesElapsedPatch)}.{nameof(Postfix)}:\n{ex}", LogLevel.Error);
                }
            }

            public static bool TryUpdateMinutesUntilReady(SObject Machine) => TryUpdateMinutesUntilReady(Machine, Machine.TryGetCombinedQuantity(out int CombinedQty) ? CombinedQty : 1);
            public static bool TryUpdateMinutesUntilReady(SObject Machine, int CombinedQty) => TryUpdateMinutesUntilReady(Machine, CombinedQty, out _, out _, out _);
            public static bool TryUpdateMinutesUntilReady(SObject Machine, int CombinedQty, out int PreviousMinutes, out int NewMinutes, out double DurationMultiplier)
            {
                PreviousMinutes = Machine.MinutesUntilReady;
                NewMinutes = PreviousMinutes;
                DurationMultiplier = 1.0;

                if (ModEntry.UserConfig.ShouldModifyProcessingSpeed(Machine))
                {
                    DurationMultiplier = 1.0 / ModEntry.UserConfig.ComputeProcessingPower(CombinedQty);
                    double TargetValue = DurationMultiplier * PreviousMinutes;
                    NewMinutes = RNGHelpers.WeightedRound(TargetValue);

                    //  Round to nearest 10 since the game processes machine outputs every 10 game minutes
                    //  EX: If NewValue = 38, then there is a 20% chance of rounding down to 30, 80% chance of rounding up to 40
                    int SmallestDigit = NewMinutes % 10;
                    NewMinutes -= SmallestDigit; // Round down to nearest 10
                    if (RNGHelpers.RollDice(SmallestDigit / 10.0))
                        NewMinutes += 10; // Round up

                    //  There seems to be a bug where there is no product if the machine is instantly done processing.
                    NewMinutes = Math.Max(10, NewMinutes); // temporary fix - require at least one 10-minute processing cycle

                    if (NewMinutes < PreviousMinutes)
                    {
                        Machine.MinutesUntilReady = NewMinutes;
                        if (NewMinutes <= 0)
                            Machine.readyForHarvest.Value = true;

                        return true;
                    }
                }

                return false;
            }
        }

        
        [HarmonyPatch(typeof(Utility), nameof(Utility.performLightningUpdate))]
        public static class PerformLightningUpdatePatch
        {
            // TODO: add logging for lightning rod patch
            public static bool Prefix(int time_of_day)
            {
                Random random = Utility.CreateRandom(Game1.uniqueIDForThisGame, Game1.stats.DaysPlayed, time_of_day);
                if (random.NextDouble() < 0.125 + Game1.player.team.AverageDailyLuck() + Game1.player.team.AverageLuckLevel() / 100.0)
                {
                    Farm.LightningStrikeEvent lightningStrikeEvent = new() { bigFlash = true };
                    Farm farm = Game1.getFarm();
                    List<Vector2> lightningRods = new List<Vector2>();
                    foreach (KeyValuePair<Vector2, SObject> pair in farm.objects.Pairs)
                    {
                        if (pair.Value.QualifiedItemId == "(BC)9")
                        {
                            lightningRods.Add(pair.Key);
                        }
                    }

                    if (lightningRods.Count > 0)
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            Vector2 vector = random.ChooseFrom(lightningRods);
                            bool isCombined = farm.objects[vector].TryGetCombinedQuantity(out int CombinedQty) && CombinedQty > 1;
                            if (farm.objects[vector].heldObject.Value == null)
                            {
                                bool changeDuration = ModEntry.UserConfig.ShouldModifyProcessingSpeed(farm.objects[vector]);
                                int duration = Utility.CalculateMinutesUntilMorning(Game1.timeOfDay);
                                if (changeDuration)
                                {
                                    double DurationMultiplier = 1.0 / ModEntry.UserConfig.ComputeProcessingPower(CombinedQty);
                                    double targetedDuration = DurationMultiplier * 1080; // 1080 = 18h = equivalent of a day in this mod (6:00 to 0:00 o'clock)
                                    int newDuration = RNGHelpers.WeightedRound(targetedDuration / 10.0) * 10; // split by 10 so it is rounded to the closest 10 minutes
                                    duration = newDuration < duration ? (newDuration < 10 ? 10 : newDuration) : duration;
                                }
                                farm.objects[vector].heldObject.Value = ItemRegistry.Create<SObject>("(O)787");
                                farm.objects[vector].minutesUntilReady.Value = duration;
                                farm.objects[vector].shakeTimer = 1000;
                                lightningStrikeEvent.createBolt = true;
                                lightningStrikeEvent.boltPosition = vector * 64f + new Vector2(32f, 0f);
                                farm.lightningStrikeEvent.Fire(lightningStrikeEvent);
                                return false;
                            }

                            bool canHoldMultipleBatteries = isCombined && ModEntry.UserConfig.ShouldModifyInputsAndOutputs(farm.objects[vector]);
                            double batteryLimitFloat = canHoldMultipleBatteries ? ModEntry.UserConfig.ComputeProcessingPower(CombinedQty) : 1;
                            int batteryLimit = batteryLimitFloat > 1 ? RNGHelpers.WeightedRound(batteryLimitFloat) : 1;
                            bool hasEmptySpaces = farm.objects[vector].heldObject.Value.Stack < batteryLimit; 
                            if (hasEmptySpaces) {
                                int previousStack = farm.objects[vector].heldObject.Value.Stack;
                                farm.objects[vector].heldObject.Value.Stack = previousStack + 1;
                                farm.objects[vector].shakeTimer = 1000;
                                lightningStrikeEvent.createBolt = true;
                                lightningStrikeEvent.boltPosition = vector * 64f + new Vector2(32f, 0f);
                                farm.lightningStrikeEvent.Fire(lightningStrikeEvent);
                                return false;
                            }
                        }
                    }

                    if (random.NextDouble() < 0.25 - Game1.player.team.AverageDailyLuck() - Game1.player.team.AverageLuckLevel() / 100.0)
                    {
                        try
                        {
                            if (Utility.TryGetRandom(farm.terrainFeatures, out var tile, out var feature))
                            {
                                if (feature is FruitTree fruitTree)
                                {
                                    fruitTree.struckByLightningCountdown.Value = 4;
                                    fruitTree.shake(tile, doEvenIfStillShaking: true);
                                    lightningStrikeEvent.createBolt = true;
                                    lightningStrikeEvent.boltPosition = tile * 64f + new Vector2(32f, -128f);
                                }
                                else
                                {
                                    Crop crop = (feature as HoeDirt)?.crop;
                                    bool num = crop != null && !crop.dead.Value;
                                    if (feature.performToolAction(null, 50, tile))
                                    {
                                        lightningStrikeEvent.destroyedTerrainFeature = true;
                                        lightningStrikeEvent.createBolt = true;
                                        farm.terrainFeatures.Remove(tile);
                                        lightningStrikeEvent.boltPosition = tile * 64f + new Vector2(32f, -128f);
                                    }

                                    if (num && crop.dead.Value)
                                    {
                                        lightningStrikeEvent.createBolt = true;
                                        lightningStrikeEvent.boltPosition = tile * 64f + new Vector2(32f, 0f);
                                    }
                                }
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }

                    farm.lightningStrikeEvent.Fire(lightningStrikeEvent);
                }
                else if (random.NextDouble() < 0.1)
                {
                    Farm.LightningStrikeEvent lightningStrikeEventSmall = new() { smallFlash = true };
                    Farm farm = Game1.getFarm();
                    farm.lightningStrikeEvent.Fire(lightningStrikeEventSmall);
                }
                return false;
            }
            private static bool TryUpdateMinutesUntilReady(SObject Machine, int CombinedQty, out int PreviousMinutes, out int NewMinutes, out double DurationMultiplier)
            {
                PreviousMinutes = Machine.MinutesUntilReady;
                NewMinutes = PreviousMinutes;
                DurationMultiplier = 1.0;

                if (ModEntry.UserConfig.ShouldModifyProcessingSpeed(Machine))
                {
                    DurationMultiplier = 1.0 / ModEntry.UserConfig.ComputeProcessingPower(CombinedQty);
                    double TargetValue = DurationMultiplier * PreviousMinutes;
                    NewMinutes = RNGHelpers.WeightedRound(TargetValue);

                    //  Round to nearest 10 since the game processes machine outputs every 10 game minutes
                    //  EX: If NewValue = 38, then there is a 20% chance of rounding down to 30, 80% chance of rounding up to 40
                    int SmallestDigit = NewMinutes % 10;
                    NewMinutes -= SmallestDigit; // Round down to nearest 10
                    if (RNGHelpers.RollDice(SmallestDigit / 10.0))
                        NewMinutes += 10; // Round up

                    //  There seems to be a bug where there is no product if the machine is instantly done processing.
                    NewMinutes = Math.Max(10, NewMinutes); // temporary fix - require at least one 10-minute processing cycle

                    if (NewMinutes < PreviousMinutes)
                    {
                        Machine.MinutesUntilReady = NewMinutes;
                        if (NewMinutes <= 0)
                            Machine.readyForHarvest.Value = true;

                        return true;
                    }
                }

                return false;
            }
        }
    
        public static class JunimaticCompatibilityPatch
        {
            static Dictionary<Type, PropertyInfo> Cache = new Dictionary<Type, PropertyInfo>();
            private static readonly string CoalQualifiedId = "(O)382";

            public static void Postfix(object __instance, object storage, Func<Item, bool> isShinyTest, ref List<Item> __result)
            {
                Type junObjectMachineType = __instance.GetType();
                Type junGameStorageType = storage.GetType();
                if (!Cache.TryGetValue(junObjectMachineType, out PropertyInfo machine))
                {
                    machine = AccessTools.Property(junObjectMachineType, "Machine");
                    Cache[junObjectMachineType] = machine;
                }
                if (!Cache.TryGetValue(junGameStorageType, out PropertyInfo inventory))
                {
                    inventory = AccessTools.Property(junGameStorageType, "SafeInventory");
                    Cache[junGameStorageType] = inventory;
                }

                if (machine?.GetValue(__instance) is not SObject Machine) return;
                if (inventory?.GetValue(storage) is not IEnumerable<Item> StorageInventory) return;

                if (Machine.IsTapper() || !Machine.TryGetCombinedQuantity(out int CombinedQty) || CombinedQty <= 1 || !ModEntry.UserConfig.ShouldModifyInputsAndOutputs(Machine))
                    return;
                        
                Inventory sourceInventory = new();
                sourceInventory.AddRange(StorageInventory.Where(i => !isShinyTest(i)).ToArray());
                double MaxMultiplier = ModEntry.UserConfig.ComputeProcessingPower(CombinedQty);
                bool MultiplyCoalInputs = ModEntry.UserConfig.FurnaceMultiplyCoalInputs;

                foreach (var item in __result)
                {
                    int ItemQuantity = sourceInventory.CountId(item.QualifiedItemId);
                    MaxMultiplier = Math.Min(MaxMultiplier, ItemQuantity * 1.0 / item.Stack);
                }
                if (MaxMultiplier > 1.0)
                {
                    foreach (var item in __result)
                    {
                        if (!MultiplyCoalInputs && item.QualifiedItemId == CoalQualifiedId)
                                continue;
                        item.Stack *= RNGHelpers.WeightedRound(MaxMultiplier);
                    }
                }

            }
        }
    }

    /// <summary>Intended to detect the moment that a CrabPots output is ready for collecting, and at that moment, apply the appropriate multiplier to the output item's stack size based on the machine's combined quantity.</summary>
    [HarmonyPatch(typeof(SObject), nameof(SObject.minutesElapsed))]
    public static class MinutesElapsedPatch
    {
        private static bool? WasReadyForHarvest = null;

        internal const int MinutesPerHour = 60;

        /// <summary>Returns the decimal number of hours between the given times (<paramref name="Time1"/> - <paramref name="Time2"/>).<para/>
        /// For example: 800-624 = 1hr36m=1.6 hours</summary>
        internal static double HoursDifference(int Time1, int Time2)
        {
            return Time1 / 100 - Time2 / 100 + (double)(Time1 % 100 - Time2 % 100) / MinutesPerHour;
        }

        /// <summary>Adds the given number of decimal hours to the given time, and returns the result in the same format that the game uses to store time (such as 650=6:50am)<para/>
        /// EX: AddHours(640, 1.5) = 810 (6:40am + 1.5 hours = 8:10am)</summary>
        internal static int AddHours(int Time, double Hours, bool RoundUpToMultipleOf10)
        {
            int OriginalHours = Time / 100;
            int HoursToAdd = (int)Hours;

            int OriginalMinutes = Time % 100;
            int MinutesToAdd = (int)((Hours - Math.Floor(Hours)) * MinutesPerHour);

            int TotalHours = OriginalHours + HoursToAdd;
            int TotalMinutes = OriginalMinutes + MinutesToAdd;
            if (RoundUpToMultipleOf10 && TotalMinutes % 10 != 0)
            {
                TotalMinutes += 10 - TotalMinutes % 10;
            }

            while (TotalMinutes >= MinutesPerHour)
            {
                TotalHours++;
                TotalMinutes -= MinutesPerHour;
            }

            return TotalHours * 100 + TotalMinutes;
        }

        /// <summary>The time of day that CrabPots should begin processing.</summary>
        private const int CrabPotDayStartTime = 600; // 6am
        /// <summary>The time of day that CrabPots should stop processing.</summary>
        private const int CrabPotDayEndTime = 2400; // 12am midnight (I know you can technically stay up until 2600=2am, but it seems unfair to the player to force them to stay up that late to collect from their crab pots)
        internal static readonly double CrabPotHoursPerDay = HoursDifference(CrabPotDayEndTime, CrabPotDayStartTime);

        public static bool Prefix(SObject __instance, int minutes)
        {
            try
            {
                if (__instance is CrabPot CrabPotInstance && CrabPotInstance.IsCombinedMachine() 
                    && Game1.IsMasterGame && !CrabPotInstance.readyForHarvest.Value && !CrabPotInstance.NeedsBait(null)
                    && ModEntry.UserConfig.ShouldModifyProcessingSpeed(CrabPotInstance))
                {
                    if (Game1.newDay)
                    {
                        CrabPotInstance.TryGetProcessingInterval(out double Power, out double IntervalHours, out int IntervalMinutes);
                        CrabPot_DayUpdatePatch.InvokeDayUpdate(CrabPotInstance);
                        ModEntry.Logger.Log($"Forced {nameof(CrabPot)}.{nameof(CrabPot.DayUpdate)} to execute at start of a new day for {nameof(CrabPot)} with Power={(Power * 100).ToString("0.##")}% (Interval={IntervalMinutes})", ModEntry.InfoLogLevel);
                    }
                    else
                    {
                        int CurrentTime = Game1.timeOfDay;
                        if (CurrentTime >= CrabPotDayStartTime && CurrentTime < CrabPotDayEndTime)
                        {
                            CrabPotInstance.TryGetProcessingInterval(out double Power, out double IntervalHours, out int IntervalMinutes);

                            //  Example:
                            //  If Power = 360% (3.6), and the crab pot can process items from 6am to 12am (18 hours), then we'd want to call DayUpdate once every 18/3.6=5.0 hours.
                            //  So the times to check for would be 600 (6am), 600+500=1100 (11am), 600+500+500=1600 (4pm), 600+500+500+500=2100 (9pm)
                            int Time = CrabPotDayStartTime;
                            while (Time <= CurrentTime)
                            {
                                if (CurrentTime == Time)
                                {
                                    CrabPot_DayUpdatePatch.InvokeDayUpdate(CrabPotInstance);
                                    ModEntry.Logger.Log($"Forced {nameof(CrabPot)}.{nameof(CrabPot.DayUpdate)} to execute at Time={CurrentTime} for {nameof(CrabPot)} with Power={(Power * 100).ToString("0.##")}% (Interval={IntervalMinutes})", ModEntry.InfoLogLevel);
                                    break;
                                }
                                else
                                    Time = AddHours(Time, IntervalHours, true);
                            }
                        }
                    }
                }

                WasReadyForHarvest = __instance.readyForHarvest.Value;
                return true;
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log(string.Format("Unhandled Error in {0}.{1}:\n{2}", nameof(MinutesElapsedPatch), nameof(Prefix), ex), LogLevel.Error);
                return true;
            }
        }

        public static void Postfix(SObject __instance)
        {
            try
            {
                if (WasReadyForHarvest == false && __instance.readyForHarvest.Value == true)
                {

                }
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log(string.Format("Unhandled Error in {0}.{1}:\n{2}", nameof(MinutesElapsedPatch), nameof(Postfix), ex), LogLevel.Error);
            }
        }
    }

    /// <summary>Intended to detect the moment that a machine's MinutesUntilReady is set increased, and at that moment,
    /// reduce the MinutesUntilReady by a factor corresponding to the combined machine's processing power.<para/>
    /// This action only takes effect if the config settings are set to <see cref="ProcessingMode.IncreaseSpeed"/>, or if the machine is an exclusion.<para/>
    /// See also: <see cref="UserConfig.ProcessingMode"/>, <see cref="UserConfig.ProcessingModeExclusions"/></summary>
    public static class MinutesUntilReadyPatch
    {
        private static readonly HashSet<Cask> CurrentlyModifying = new HashSet<Cask>();

        public static void Postfix(SObject __instance)
        {
            try
            {
                if (Game1.IsMasterGame && __instance is Cask CaskInstance)
                {
                    CaskInstance.agingRate.fieldChangeEvent += (field, oldValue, newValue) =>
                    {
                        if (!Game1.IsMasterGame) return;
                        try
                        {
                            //  Prevent recursive fieldChangeEvents from being invoked when our code sets Cask.agingRate.Value
                            if (CurrentlyModifying.Contains(CaskInstance))
                                return;

                            if (Context.IsWorldReady && oldValue != newValue) //Context.IsMainPlayer && 
                            {
                                if (ModEntry.UserConfig.ShouldModifyProcessingSpeed(__instance) && __instance.TryGetCombinedQuantity(out int CombinedQuantity))
                                {
                                    if (!CaskInstance.TryGetDefaultAgingRate(out float DefaultAgingRate))
                                        DefaultAgingRate = -1;
                                    bool IsTrackedValueChange = false;
                                    ModEntry.Logger.Log($"Cask Aging Rate Change triggered from {oldValue} to {newValue}. Default aging rate set for this cask: {DefaultAgingRate} (heldItem: {CaskInstance.heldObject.Value?.QualifiedItemId})", ModEntry.InfoLogLevel);

                                    if (CaskInstance.heldObject.Value == null && newValue > 0) // Handle the first time agingRate is initialized
                                    {
                                        IsTrackedValueChange = true;
                                        DefaultAgingRate = newValue;
                                        CaskInstance.SetDefaultAgingRate(DefaultAgingRate); // don't set if not MasterGame in Debugging!
                                    }
                                    else if (newValue == DefaultAgingRate) // Handle cases where the game tries to reset the agingRate
                                        IsTrackedValueChange = true;
                                    // if (!Game1.IsMasterGame) return; // later call for debugging

                                    if (IsTrackedValueChange)
                                    {
                                        double DurationMultiplier = ModEntry.UserConfig.ComputeProcessingPower(CombinedQuantity);
                                        float NewAgingRate = (float)(DurationMultiplier * DefaultAgingRate);
                                        float CurrentAgingRate = newValue; //CaskInstance.agingRate.Value;

                                        if (NewAgingRate != CurrentAgingRate)
                                        {
                                            try
                                            {
                                                CurrentlyModifying.Add(CaskInstance);
                                                CaskInstance.agingRate.Value = NewAgingRate;
                                            }
                                            finally { CurrentlyModifying.Remove(CaskInstance); }

                                            ModEntry.Logger.Log(string.Format("Set {0} agingRate from {1} to {2} based on default agingRate {3} ({4}%)",
                                                __instance.Name, CurrentAgingRate, NewAgingRate, DefaultAgingRate, (DurationMultiplier * 100.0).ToString("0.##")), ModEntry.InfoLogLevel);
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception Error)
                        {
                            ModEntry.Logger.Log(string.Format("Unhandled Error in {0}.{1}.FieldChangeEvent(Cask):\n{2}", nameof(MinutesUntilReadyPatch), nameof(Postfix), Error), LogLevel.Error);
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Unhandled Error in {nameof(MinutesUntilReadyPatch)}.{nameof(Postfix)}:\n{ex}", LogLevel.Error);
            }
        }
    }

    [HarmonyPatch(typeof(CrabPot), nameof(CrabPot.DayUpdate))]
    public static class CrabPot_DayUpdatePatch
    {
        /// <summary>Data that is retrieved just before <see cref="CrabPot.DayUpdate"/> executes.</summary>
        private class DayUpdateParameters
        {
            public SObject CrabPot { get; }
            public SObject PreviousHeldObject { get; }
            public SObject CurrentHeldObject { get { return CrabPot?.heldObject.Value; } }
            public int PreviousHeldObjectQuantity { get; }
            public int CurrentHeldObjectQuantity { get { return CurrentHeldObject == null ? 0 : CurrentHeldObject.Stack; } }

            public DayUpdateParameters(CrabPot CrabPot)
            {
                this.CrabPot = CrabPot;
                this.PreviousHeldObject = CrabPot.heldObject.Value;
                this.PreviousHeldObjectQuantity = PreviousHeldObject != null ? PreviousHeldObject.Stack : 0;
            }
        }

        private static DayUpdateParameters PrefixData { get; set; }

        public static bool Prefix(CrabPot __instance)
        {
            try
            {
                PrefixData = new DayUpdateParameters(__instance);
                if (__instance.IsCombinedMachine())
                {
                    if (CurrentlyModifying.Contains(__instance) || !ModEntry.UserConfig.ShouldModifyProcessingSpeed(__instance))
                        return true;
                    else
                        return false;
                }
                else
                    return true;
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log(string.Format("Unhandled Error in {0}.{1}:\n{2}", nameof(CrabPot_DayUpdatePatch), nameof(Prefix), ex), LogLevel.Error);
                return true;
            }
        }

        public static void Postfix(CrabPot __instance)
        {
            try
            {
                //  Check if the output item was just set
                if (PrefixData != null && PrefixData.CrabPot == __instance && PrefixData.PreviousHeldObject == null && PrefixData.CurrentHeldObject != null)
                {
                    //  Modify the output quantity based on the combined machine's processing power
                    if (__instance.IsCombinedMachine() && ModEntry.UserConfig.ShouldModifyInputsAndOutputs(__instance) && __instance.TryGetCombinedQuantity(out int CombinedQuantity))
                    {
                        double Power = ModEntry.UserConfig.ComputeProcessingPower(CombinedQuantity);
                        double DesiredNewValue = PrefixData.CurrentHeldObjectQuantity * Power;
                        int RoundedNewValue = RNGHelpers.WeightedRound(DesiredNewValue);
                        __instance.heldObject.Value.Stack = RoundedNewValue;
                        ModEntry.LogTrace(CombinedQuantity, PrefixData.CrabPot, PrefixData.CrabPot.TileLocation, "HeldObject.Stack", PrefixData.CurrentHeldObjectQuantity,
                            DesiredNewValue, RoundedNewValue, Power);
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log(string.Format("Unhandled Error in {0}.{1}:\n{2}", nameof(CrabPot_DayUpdatePatch), nameof(Postfix), ex), LogLevel.Error);
            }
        }

        private static HashSet<CrabPot> CurrentlyModifying = new HashSet<CrabPot>();

        internal static void InvokeDayUpdate(CrabPot instance)
        {
            if (instance == null)
                return;

            try
            {
                CurrentlyModifying.Add(instance);
                instance.DayUpdate();
            }
            finally { CurrentlyModifying.Remove(instance); }
        }
    }
}
