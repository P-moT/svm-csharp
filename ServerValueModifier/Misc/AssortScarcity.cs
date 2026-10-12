using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using TraderID = SPTarkov.Server.Core.Models.Enums.Traders;

namespace ServerValueModifier
{
    /// <summary>
    /// Scarcity mode for trader stock (Traders > Randomize Assort).
    /// Every restock, each offer rolls whether it is in stock at all, and if so, how many.
    /// Out of stock = StackObjectsCount 0, so the offer stays visible but cannot be bought.
    /// Used both at server start (Sections.Traders) and on every trader restock (TraderPatch).
    /// </summary>
    public static class AssortScarcity
    {
        // Tuning knobs - InStockChance is 0..1, quantities are inclusive.
        public record StockRule(double InStockChance, int Min, int Max);

        public static readonly StockRule Ammo = new(0.30, 10, 90); // Loose rounds
        public static readonly StockRule AmmoBox = new(0.30, 1, 1);
        public static readonly StockRule Gear = new(0.25, 1, 1);   // Weapons, armor, plates, helmets, rigs, backpacks, weapon mods
        public static readonly StockRule Other = new(0.35, 1, 1);  // Meds, provisions, barter items, keys, containers, etc.

        // Boon - chance an in-stock LL2+ offer is sold one loyalty level lower for this restock.
        public static double LoyaltyBumpChance = 0.1;

        // Print a summary to the server console on every roll (startup + each restock).
        public static bool LogRolls = true;

        // Original loyalty levels per trader. ResetExpiredTrader only restores Items, so bumps would stack without this.
        private static readonly Dictionary<MongoId, Dictionary<MongoId, int>> OriginalLoyaltyLevels = new();

        // Base class nodes from templates/items.json
        private static readonly MongoId AmmoClass = "5485a8684bdc2da71d8b4567";
        private static readonly MongoId AmmoBoxClass = "543be5cb4bdc2deb348b4568";
        private static readonly MongoId WeaponClass = "5422acb9af1c889c16000029";
        private static readonly MongoId EquipmentClass = "543be5f84bdc2dd4348b456a"; // Covers ArmoredEquipment > Armor/Headwear/ArmorPlate, backpacks, rigs
        private static readonly MongoId ModClass = "5448fe124bdc2da5018b4567";
        private static readonly MongoId MoneyClass = "543be5dd4bdc2deb348b4569";

        /// <summary>
        /// Re-roll stock for every root offer of a trader. Lightkeeper, Fence and Ref are left alone.
        /// </summary>
        public static void Apply(Trader trader, Dictionary<MongoId, TemplateItem> templates, ISptLogger<SVM>? logger = null, ItemHelper? itemHelper = null)
        {
            if (trader?.Assort?.Items is null || trader.Base.Id == TraderID.LIGHTHOUSEKEEPER || trader.Base.Id == TraderID.FENCE || trader.Base.Id == TraderID.REF)
            {
                return;
            }
            var loyaltyLevels = trader.Assort.LoyalLevelItems;
            if (!OriginalLoyaltyLevels.TryGetValue(trader.Base.Id, out var originalLevels))
            {
                originalLevels = new();
                if (loyaltyLevels is not null)
                {
                    foreach (var level in loyaltyLevels)
                    {
                        originalLevels[level.Key] = level.Value;
                    }
                }
                OriginalLoyaltyLevels[trader.Base.Id] = originalLevels;
            }
            //Quest-locked offers stay hidden regardless of loyalty level, so they can't receive the boon.
            //Read live, so SVM's "Unlock quest assort" option (which empties these) is respected.
            HashSet<MongoId> questLocked = [];
            if (trader.QuestAssort is not null)
            {
                foreach (var status in trader.QuestAssort.Values)
                {
                    if (status is null) continue;
                    foreach (var questOffer in status.Keys)
                    {
                        questLocked.Add(questOffer);
                    }
                }
            }
            int offers = 0, inStock = 0;
            List<Item> boons = [];
            foreach (Item offer in trader.Assort.Items)
            {
                if (offer.ParentId != "hideout")//Only root items are offers, the rest are attachments of presets
                {
                    continue;
                }
                StockRule? rule = GetRule(offer.Template, templates);
                if (rule is null)//Currency exchange offers keep their stock
                {
                    continue;
                }
                offer.Upd ??= new();
                offer.Upd.UnlimitedCount = false;
                offer.Upd.StackObjectsCount = Random.Shared.NextDouble() < rule.InStockChance
                    ? Random.Shared.Next(rule.Min, rule.Max + 1)
                    : 0;
                offers++;
                if (offer.Upd.StackObjectsCount > 0) inStock++;

                //Loyalty boon - always start from the original level, so a bump lasts one restock only
                if (loyaltyLevels is not null && originalLevels.TryGetValue(offer.Id, out int originalLevel))
                {
                    bool bumped = offer.Upd.StackObjectsCount > 0 && originalLevel > 1 && !questLocked.Contains(offer.Id) && Random.Shared.NextDouble() < LoyaltyBumpChance;
                    loyaltyLevels[offer.Id] = bumped ? originalLevel - 1 : originalLevel;
                    if (bumped)
                    {
                        boons.Add(offer);
                    }
                }
            }
            if (LogRolls && logger is not null)
            {
                logger.LogWithColor($"[SVM] Scarcity - {trader.Base.Nickname}: {inStock}/{offers} offers in stock, {boons.Count} loyalty boon(s)", Spectre.Console.Color.Blue);
                foreach (Item boon in boons)
                {
                    int level = originalLevels[boon.Id];
                    //Same item can be sold in several offers (different price, barter, preset) - list the others so the right one can be found in game
                    var otherLevels = trader.Assort.Items
                        .Where(other => other.ParentId == "hideout" && other.Template == boon.Template && other.Id != boon.Id)
                        .Select(other => originalLevels.TryGetValue(other.Id, out int otherLevel) ? $"LL{otherLevel}{(questLocked.Contains(other.Id) ? " quest" : "")}" : "LL?")
                        .ToList();
                    string others = otherLevels.Count > 0 ? $" | other offers of this item: {string.Join(", ", otherLevels)}" : "";
                    logger.LogWithColor($"[SVM]     Boon: {GetName(boon.Template, itemHelper)} x{boon.Upd!.StackObjectsCount} for {GetPrice(trader, boon.Id, itemHelper)} (LL{level} -> LL{level - 1}){others}", Spectre.Console.Color.Green);
                }
            }
        }

        private static string GetPrice(Trader trader, MongoId offerId, ItemHelper? itemHelper)
        {
            if (trader.Assort.BarterScheme is null || !trader.Assort.BarterScheme.TryGetValue(offerId, out var schemes) || schemes.Count == 0)
            {
                return "?";
            }
            var cost = schemes[0];
            if (cost.Count == 1 && cost[0].Template == "5449016a4bdc2d6f028b456f") return $"{cost[0].Count:N0} RUB";
            if (cost.Count == 1 && cost[0].Template == "5696686a4bdc2da3298b456a") return $"{cost[0].Count:N0} USD";
            if (cost.Count == 1 && cost[0].Template == "569668774bdc2da2298b4568") return $"{cost[0].Count:N0} EUR";
            return "barter: " + string.Join(" + ", cost.Select(part => $"{part.Count}x {GetName(part.Template, itemHelper)}"));
        }

        private static string GetName(MongoId tpl, ItemHelper? itemHelper)
        {
            try//Logging must never break the roll, e.g. if locales aren't loaded yet at startup
            {
                return itemHelper?.GetItemName(tpl) ?? tpl.ToString();
            }
            catch
            {
                return tpl.ToString();
            }
        }

        private static StockRule? GetRule(MongoId tpl, Dictionary<MongoId, TemplateItem> templates)
        {
            // Walk up the parent chain, nearest class wins. Depth cap guards against broken modded templates.
            MongoId? current = tpl;
            for (int depth = 0; depth < 15 && current is not null && templates.TryGetValue(current.Value, out TemplateItem? template); depth++)
            {
                MongoId id = template.Id;
                if (id == MoneyClass) return null;
                if (id == AmmoClass) return Ammo;
                if (id == AmmoBoxClass) return AmmoBox;
                if (id == WeaponClass || id == EquipmentClass || id == ModClass) return Gear;
                current = template.Parent;
            }
            return Other;
        }
    }
}
