using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using Path = System.IO.Path;

namespace _itemValuation;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.acidphantasm.itemvaluation";
    public override string Name { get; init; } = "Item Valuation";
    public override string Author { get; init; } = "acidphantasm";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("2.0.1");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.12");
    public override List<string>? Incompatibilities { get; init; } = ["com.odt.iteminfo"];
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; }
    public override string? License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostSptModLoader + 100)]
public class ItemValuation(
    ISptLogger<ItemValuation> logger,
    DatabaseService databaseService,
    LocaleService localeService,
    ItemHelper itemHelper,
    ConfigServer configServer,
    PresetHelper presetHelper,
    RandomUtil randomUtil,
    HandbookHelper handbookHelper,
    RagfairServerHelper ragfairServerHelper,
    ModHelper modHelper,
    FileUtil fileUtil,
    IReadOnlyList<SptMod> installedMods,
    ICloner cloner)
    : IOnLoad
{
    private Dictionary<MongoId, double>? _originalPrices;
    private Dictionary<string, string>? _originalLocales;
    private ModConfig? _modConfig;
    private bool _colourConverterInstalled;
    private bool _liveFleaPricesInstalled;

    private int _itemsUpdated = 0;
    
    private readonly RagfairConfig _ragfairConfig = configServer.GetConfig<RagfairConfig>();

    private readonly Dictionary<MongoId, TraderPriceTableDetails> _highestTraderPriceItems = new();
    
    private readonly List<MongoId> _bannedBaseClasses =
    [
        BaseClasses.LOOT_CONTAINER,
        BaseClasses.STASH,
        BaseClasses.POCKETS,
        BaseClasses.RANDOM_LOOT_CONTAINER,
        BaseClasses.BUILT_IN_INSERTS,
        BaseClasses.HIDEOUT_AREA_CONTAINER
    ];

    private readonly List<string> _validArmourSlots =
    [
        "helmet_top",
        "helmet_back",
        "helmet_ears",
        "helmet_eyes",
        "helmet_jaw",
        "front_plate",
        "back_plate",
        "soft_armor_front",
        "soft_armor_back",
        "soft_armor_left",
        "soft_armor_right",
        "collar",
        "groin",
        "groin_back",
        "shoulder_l",
        "shoulder_r"
    ];

    public Task OnLoad()
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        _modConfig = modHelper.GetJsonDataFromFile<ModConfig>(pathToMod, "config.json");
        
        _colourConverterInstalled = IsColourConverterInstalled();
        _liveFleaPricesInstalled = IsLiveFleaPricesInstalled();
        if (!_colourConverterInstalled) MakeAdjustmentsForColourConverterMissing();
        
        StoreOriginalTables();
        SetColours(true);

        if (!_liveFleaPricesInstalled) return Task.CompletedTask;
        var updateTask = new Task(async () =>
        {
            while (true)
            {
                Thread.Sleep(60 * 60 * 1000);
                SetColours();
            }
        });
        updateTask.Start();

        return Task.CompletedTask;
    }
    private void StoreOriginalTables()
    {
        _originalPrices = cloner.Clone(databaseService.GetPrices());
        _originalLocales = cloner.Clone(localeService.GetLocaleDb());
    }

    private void MakeAdjustmentsForColourConverterMissing()
    {
        _modConfig.BadColour = "grey";
        _modConfig.PoorColour = "default";
        _modConfig.FairColour = "green";
        _modConfig.GoodColour = "blue";
        _modConfig.VeryGoodColour = "violet";
        _modConfig.ExceptionalColour = "yellow";
        _modConfig.FleaBannedColour = "tracerRed";
        logger.Info("ColorConverterAPI is not found. If you want custom colours, install ColorConverterAPI.");
    }
    private void SetColours(bool firstRun = false)
    {
        var sw = Stopwatch.StartNew();
        var itemTable = databaseService.GetItems();
        var priceTable = databaseService.GetPrices();
        var handbookTable = databaseService.GetHandbook();
        
        var baseClasses = typeof(BaseClasses).GetFields(BindingFlags.Static | BindingFlags.Public)
            .Select(x => x.GetValue(null)).Cast<MongoId>();

        foreach (var (mongoId, templateItem) in itemTable)
        {
            if (itemHelper.IsOfBaseclasses(mongoId, _bannedBaseClasses)) continue;
            if (templateItem.Parent.IsEmpty || baseClasses.Contains(mongoId)) continue;
            if (!firstRun)
            {
                if (!_originalPrices.TryGetValue(mongoId, out var originalPrice) || !priceTable.TryGetValue(mongoId, out var newPrice))
                    continue;

                if ((int)originalPrice == (int)newPrice)
                    continue;
            }
            
            double price = 0;
            var handBookPrice = handbookTable.Items.Find(x => x.Id == mongoId)?.Price ?? 1;
            
            if (priceTable.TryGetValue(mongoId, out var pricesPrice))
            {
                price = pricesPrice;
            }
            else
            {
                price = handbookTable.Items.Find(x => x.Id == mongoId)?.Price ?? 1;
            }
            
            foreach (var (key, value) in _ragfairConfig.Dynamic.UnreasonableModPrices)
            {
                if (!itemHelper.IsOfBaseclass(mongoId, key)) continue;
                if (price > handBookPrice *
                    _ragfairConfig.Dynamic.UnreasonableModPrices[key].HandbookPriceOverMultiplier)
                {
                    price = handBookPrice * _ragfairConfig.Dynamic.UnreasonableModPrices[key].NewPriceHandbookMultiplier;
                }
            }

            var traderPriceInfo = GetHighestTraderPriceRouble(mongoId);

            if (traderPriceInfo is null)
            {
                continue;
            }
            if (_modConfig.UseTraderPriceColours)
                price = traderPriceInfo.traderPrice;

            var height = itemTable[mongoId].Properties.Height;
            var width = itemTable[mongoId].Properties.Width;

            if (height is null || width is null) continue;
            
            var pricePerSlot = (double)Math.Round(price / (height.Value * width.Value));
            var validFleaItem = ragfairServerHelper.IsItemValidRagfairItem(itemHelper.GetItem(mongoId));

            var newBackgroundColour = String.Empty;
            var oldBackgroundColour = itemTable[mongoId].Properties.BackgroundColor;
            double descriptionPrice = 0;
            var addDescription = false;
            var perSlotDescription = false;
            if (itemHelper.IsOfBaseclass(mongoId, BaseClasses.WEAPON))
            {
                newBackgroundColour = GetWeaponColour(price, validFleaItem, oldBackgroundColour);
                descriptionPrice = price;
                addDescription = true;
            }
            else if (itemHelper.IsOfBaseclass(mongoId, BaseClasses.AMMO))
            {
                var penetrationValue = itemTable[mongoId].Properties.PenetrationPower;
                newBackgroundColour = GetAmmoColour(penetrationValue, validFleaItem, oldBackgroundColour);
                descriptionPrice = price;
                addDescription = true;
            }
            else if (itemHelper.IsOfBaseclass(mongoId, BaseClasses.KEY))
            {
                newBackgroundColour = GetKeyColour(price, validFleaItem, oldBackgroundColour);
                descriptionPrice = price;
                addDescription = true;
            }
            else if (itemHelper.IsOfBaseclasses(mongoId, [BaseClasses.ARMORED_EQUIPMENT, BaseClasses.VEST]))
            {
                var armourClass = itemTable[mongoId].Properties?.ArmorClass ?? 0;
                if (armourClass == 0)
                {
                    var itemSlots = itemTable[mongoId].Properties?.Slots;
                    if (itemSlots == null || !itemSlots.Any(slot => _validArmourSlots.Contains(slot.Name.ToLowerInvariant())))
                    {
                        newBackgroundColour = GetItemColour(pricePerSlot, validFleaItem, oldBackgroundColour);
                        FinalizeItemAndLocales(pricePerSlot, validFleaItem, mongoId, traderPriceInfo, true);
                        itemTable[mongoId].Properties.BackgroundColor = newBackgroundColour;
                        _itemsUpdated++;
                        continue;
                    }

                    var compatiblePlateTplPool = (itemSlots?
                        .Where(slot => _validArmourSlots.Contains(slot.Name.ToLowerInvariant()))
                        .Select(slot => slot.Properties?.Filters?.FirstOrDefault(f => f.Plate.HasValue)?.Plate)
                        .Where(p => p.HasValue)
                        .Select(p => p.Value)
                        .ToList()) ?? new List<MongoId>();
                    
                    if (compatiblePlateTplPool.Count == 0)
                        continue;
                    
                    var platesFromDb = compatiblePlateTplPool
                        .Select(itemHelper.GetItem)
                        .Where(item => item.Key)
                        .Select(item => item.Value)
                        .ToList();
                    
                    var minMaxPlates = GetMinMaxArmorPlateClass(platesFromDb);
                    
                    newBackgroundColour = GetArmourColour(minMaxPlates.Max, validFleaItem, oldBackgroundColour);
                }
                else
                {
                    newBackgroundColour = GetArmourColour(armourClass, validFleaItem, oldBackgroundColour);
                }
                descriptionPrice = price;
                addDescription = true;
            }
            else if (itemHelper.IsOfBaseclass(mongoId, BaseClasses.MONEY))
            {
                newBackgroundColour = "black";
            }
            else
            {
                newBackgroundColour = GetItemColour(pricePerSlot, validFleaItem, oldBackgroundColour);
                descriptionPrice = pricePerSlot;
                addDescription = true;
                perSlotDescription = true;
            }

            if (addDescription)
            {
                FinalizeItemAndLocales(descriptionPrice, validFleaItem, mongoId, traderPriceInfo, perSlotDescription);
            }
            
            _itemsUpdated++;
            
            if (string.IsNullOrEmpty(newBackgroundColour)) continue;
            itemTable[mongoId].Properties.BackgroundColor = newBackgroundColour;
        }

        var time = sw.ElapsedMilliseconds;
        sw.Stop();
        _originalPrices = cloner.Clone(databaseService.GetPrices());
        Console.WriteLine($"[Item Valuation] Updated {_itemsUpdated} items in {time} ms");
        _itemsUpdated = 0;
    }

    private TraderPriceTableDetails? GetHighestTraderPriceRouble(MongoId itemId)
    {
        var preset = presetHelper.GetDefaultPreset(itemId);
        var traders = databaseService.GetTraders();
        var actualHandbookPrice = handbookHelper.GetTemplatePrice(itemId);

        if (_highestTraderPriceItems.TryGetValue(itemId, out var highestPriceDetails)) return highestPriceDetails;
        
        foreach (var (traderId, traderDetails) in traders)
        {
            var traderBase = traderDetails.Base;
            if (traderBase.ItemsBuy is null) continue;
            if (!itemHelper.IsOfBaseclasses(itemId, traderBase.ItemsBuy.Category.ToList())) continue;
            
            var traderBuyBackPricePercent = 100 - traderBase.LoyaltyLevels[0].BuyPriceCoefficient;

            if (preset is not null)
            {
                actualHandbookPrice = 0;
                foreach (var item in preset.Items)
                {
                    actualHandbookPrice += handbookHelper.GetTemplatePrice(item.Template);
                }
            }

            var priceTraderBuysItemsAt =
                Math.Round(randomUtil.GetPercentOfValue(traderBuyBackPricePercent ?? 0, actualHandbookPrice));

            var details = new TraderPriceTableDetails
            {
                traderName = traderDetails.Base.Nickname ?? "Unknown",
                traderPrice = priceTraderBuysItemsAt
            };

            if (_highestTraderPriceItems.TryGetValue(itemId, out var value) && priceTraderBuysItemsAt > _highestTraderPriceItems[itemId].traderPrice)
            {
                value.traderName = traderDetails.Base.Nickname ?? "Unknown";
                value.traderPrice = priceTraderBuysItemsAt;
            }
            else
            {
                _highestTraderPriceItems.TryAdd(itemId, details);
            }
        }
        return _highestTraderPriceItems.TryGetValue(itemId, out var highestTraderPriceItemDetails) ? highestTraderPriceItemDetails : null;
    }

    private void FinalizeItemAndLocales(double? price, bool availableOnFlea, MongoId itemId, TraderPriceTableDetails traderPrice, bool perSlotDescription = false)
    {
        
        if (!_originalLocales.TryGetValue($"{itemId} Description", out var originalDescription) &&
            !_originalLocales.TryGetValue($"{itemId} description", out originalDescription))
        {
            logger.Warning($"[Item Valuation] No locale description for {itemId} - Report to the author of that item ID...skipping..");
            return;
        }
        
        foreach (var (locale, entry) in databaseService.GetLocales().Global)
        {
            var priceType = perSlotDescription ? "Per Slot:" : "Total:";
            
            var fleaText = availableOnFlea
                ? "<color=#17751b>Not Flea Banned</color>"
                : "<color=#751717>Flea Banned</color>";
            
            var traderText = traderPrice is not null
                ? $"\nTotal: {ConvertToRoubles(traderPrice.traderPrice)} @ {traderPrice.traderName}"
                : "";
            
            var newDescription =
                _modConfig.UseTraderPriceColours && traderPrice.traderPrice > 0
                    ? $"{priceType} {ConvertToRoubles(price)} @ {traderPrice.traderName}\n {fleaText} \n\n {originalDescription}"
                    : $"{priceType} {ConvertToRoubles(price)} @ Flea {traderText}\n {fleaText} \n\n {originalDescription}";

            entry.AddTransformer(transformer =>
            {
                transformer[$"{itemId} Description"] = newDescription;

                return transformer;
            });

            if (itemHelper.IsOfBaseclass(itemId, BaseClasses.AMMO) && _modConfig.DamageAndPenStatsInName)
            {
                var ammoDetails = itemHelper.GetItem(itemId);
                if (ammoDetails.Key.Equals(true))
                {
                    var damage = ammoDetails.Value.Properties.Damage;
                    var penetration = ammoDetails.Value.Properties.PenetrationPower;
                    var originalName = _originalLocales[$"{itemId} Name"];
                    var newName = $"{originalName} <color=#808080>[{damage}/{penetration}]</color>";
                    
                    entry.AddTransformer(transformer =>
                    {
                        transformer[$"{itemId} Name"] = newName;

                        return transformer;
                    });

                    if (_modConfig.DamageAndPenStatsInShortNameWarningTiny)
                    {
                        var originalShortName = _originalLocales[$"{itemId} ShortName"];
                        var newShortName = $"<sup><color=#808080>[{damage}/{penetration}]</color></sup> {originalShortName}";
                        
                        
                        entry.AddTransformer(transformer =>
                        {
                            transformer[$"{itemId} ShortName"] = newShortName;

                            return transformer;
                        });
                    }
                }
            }
        }
    }

    private string ConvertToRoubles(double? price)
    {
        NumberFormatInfo customFormat = new NumberFormatInfo();
        customFormat.CurrencySymbol = "₽";
        customFormat.CurrencyDecimalSeparator = ".";
        customFormat.CurrencyGroupSeparator = ",";
        customFormat.CurrencyDecimalDigits = 0;

        double nonNullable = price ?? 0;
        return nonNullable.ToString("C" , customFormat);
    }

    private string GetItemColour(double? pricePerSlot, bool availableOnFlea, string? oldBackgroundColour)
    {
        if (_modConfig.ColourFleaBannedItems && !availableOnFlea) return _modConfig.FleaBannedColour;
        if (!_modConfig.ColourNormalItems) return oldBackgroundColour;
        if (pricePerSlot < _modConfig.BadItemPerSlotMaxValue) return _modConfig.BadColour;
        if (pricePerSlot < _modConfig.PoorItemPerSlotMaxValue) return _modConfig.PoorColour;
        if (pricePerSlot < _modConfig.FairItemPerSlotMaxValue) return _modConfig.FairColour;
        if (pricePerSlot < _modConfig.GoodItemPerSlotMaxValue) return _modConfig.GoodColour;
        if (pricePerSlot < _modConfig.VeryGoodItemPerSlotMaxValue) return _modConfig.VeryGoodColour;
        return _modConfig.ExceptionalColour;
    }

    private string GetKeyColour(double pricePerSlot, bool availableOnFlea, string? oldBackgroundColour)
    {
        if (_modConfig.ColourFleaBannedKeys && !availableOnFlea) return _modConfig.FleaBannedColour;
        if (!_modConfig.ColourKeys) return oldBackgroundColour;
        if (pricePerSlot < _modConfig.BadKeyMaxValue) return _modConfig.BadColour;
        if (pricePerSlot < _modConfig.PoorKeyMaxValue) return _modConfig.PoorColour;
        if (pricePerSlot < _modConfig.FairKeyMaxValue) return _modConfig.FairColour;
        if (pricePerSlot < _modConfig.GoodKeyMaxValue) return _modConfig.GoodColour;
        if (pricePerSlot < _modConfig.VeryGoodKeyMaxValue) return _modConfig.VeryGoodColour;
        return _modConfig.ExceptionalColour;
    }

    private string GetArmourColour(int armourClass, bool availableOnFlea, string? oldBackgroundColour)
    {
        if (_modConfig.ColourFleaBannedArmour && !availableOnFlea) return _modConfig.FleaBannedColour;
        if (!_modConfig.ColourArmours) return oldBackgroundColour;
        if (armourClass <= _modConfig.BadArmourMaxPlateClass) return _modConfig.BadColour;
        if (armourClass <= _modConfig.PoorArmourMaxPlateClass) return _modConfig.PoorColour;
        if (armourClass <= _modConfig.FairArmourMaxPlateClass) return _modConfig.FairColour;
        if (armourClass <= _modConfig.GoodArmourMaxPlateClass) return _modConfig.GoodColour;
        if (armourClass <= _modConfig.VeryGoodArmourMaxPlateClass) return _modConfig.VeryGoodColour;
        return _modConfig.ExceptionalColour;
    }

    private string GetWeaponColour(double price, bool availableOnFlea, string? oldBackgroundColour)
    {
        if (_modConfig.ColourFleaBannedWeapons && !availableOnFlea) return _modConfig.FleaBannedColour;
        if (!_modConfig.ColourWeapons) return oldBackgroundColour;
        if (price < _modConfig.BadWeaponMaxValue) return _modConfig.BadColour;
        if (price < _modConfig.PoorWeaponMaxValue) return _modConfig.PoorColour;
        if (price < _modConfig.FairWeaponMaxValue) return _modConfig.FairColour;
        if (price < _modConfig.GoodWeaponMaxValue) return _modConfig.GoodColour;
        if (price < _modConfig.VeryGoodWeaponMaxValue) return _modConfig.VeryGoodColour;
        return _modConfig.ExceptionalColour;
    }

    private string GetAmmoColour(int? penetration, bool availableOnFlea, string? oldBackgroundColour)
    {
        if (_modConfig.ColourFleaBannedAmmo && !availableOnFlea) return _modConfig.FleaBannedColour;
        if (!_modConfig.ColourAmmo) return oldBackgroundColour;
        if (penetration <= _modConfig.BadAmmoMaxPen) return _modConfig.BadColour;
        if (penetration <= _modConfig.PoorAmmoMaxPen) return _modConfig.PoorColour;
        if (penetration <= _modConfig.FairAmmoMaxPen) return _modConfig.FairColour;
        if (penetration <= _modConfig.GoodAmmoMaxPen) return _modConfig.GoodColour;
        if (penetration <= _modConfig.VeryGoodAmmoMaxPen) return _modConfig.VeryGoodColour;
        return _modConfig.ExceptionalColour;
    }
    
    private MinMax<int> GetMinMaxArmorPlateClass(IEnumerable<TemplateItem> platePool)
    {
        var armorClasses = platePool
            .Select(p => p.Properties.ArmorClass)
            .Where(ac => ac.HasValue)
            .Select(ac => ac.Value)
            .ToList();

        if (!armorClasses.Any())
            return new MinMax<int> { Min = 0, Max = 0 };

        return new MinMax<int>
        {
            Min = armorClasses.Min(),
            Max = armorClasses.Max()
        };
    }

    private bool IsColourConverterInstalled()
    {
        var pluginName = "rairai.colorconverterapi.dll";
        try
        {
            var currentDirectory = Directory.GetCurrentDirectory();
            var currentDirInfo = new DirectoryInfo(currentDirectory);
            var parentDirectoryInfo = currentDirInfo.Parent;
            if (parentDirectoryInfo != null)
            {
                var pluginPath = Path.Combine(parentDirectoryInfo.FullName, "BepInEx", "plugins", pluginName);
            
                if (fileUtil.FileExists(pluginPath)) return true;
            }

            return false;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    private bool IsLiveFleaPricesInstalled()
    {
        return installedMods.Any(x => x.ModMetadata.ModGuid == "xyz.drakia.livefleaprices");
    }
}
public class TraderPriceTableDetails
{
    public double traderPrice;
    public string traderName;
}

public class ModConfig
{
    public bool UseTraderPriceColours { get; set; }
    
    public bool ColourNormalItems { get; set; }
    public bool ColourFleaBannedItems { get; set; }
    public double BadItemPerSlotMaxValue { get; set; }
    public double PoorItemPerSlotMaxValue { get; set; }
    public double FairItemPerSlotMaxValue { get; set; }
    public double GoodItemPerSlotMaxValue { get; set; }
    public double VeryGoodItemPerSlotMaxValue { get; set; }
    
    public bool ColourKeys { get; set; }
    public bool ColourFleaBannedKeys { get; set; }
    public double BadKeyMaxValue { get; set; }
    public double PoorKeyMaxValue { get; set; }
    public double FairKeyMaxValue { get; set; }
    public double GoodKeyMaxValue { get; set; }
    public double VeryGoodKeyMaxValue { get; set; }
    
    public bool DamageAndPenStatsInName { get; set; }
    public bool DamageAndPenStatsInShortNameWarningTiny { get; set; }
    public bool ColourAmmo { get; set; }
    public bool ColourFleaBannedAmmo { get; set; }
    public double BadAmmoMaxPen { get; set; }
    public double PoorAmmoMaxPen { get; set; }
    public double FairAmmoMaxPen { get; set; }
    public double GoodAmmoMaxPen { get; set; }
    public double VeryGoodAmmoMaxPen { get; set; }
    
    public bool ColourWeapons { get; set; }
    public bool ColourFleaBannedWeapons { get; set; }
    public double BadWeaponMaxValue { get; set; }
    public double PoorWeaponMaxValue { get; set; }
    public double FairWeaponMaxValue { get; set; }
    public double GoodWeaponMaxValue { get; set; }
    public double VeryGoodWeaponMaxValue { get; set; }
    
    public bool ColourArmours { get; set; }
    public bool ColourFleaBannedArmour { get; set; }
    public int BadArmourMaxPlateClass { get; set; }
    public int PoorArmourMaxPlateClass { get; set; }
    public int FairArmourMaxPlateClass { get; set; }
    public int GoodArmourMaxPlateClass { get; set; }
    public int VeryGoodArmourMaxPlateClass { get; set; }
    
    public required string BadColour { get; set; }
    public required string PoorColour { get; set; }
    public required string FairColour { get; set; }
    public required string GoodColour { get; set; }
    public required string VeryGoodColour { get; set; }
    public required string ExceptionalColour { get; set; }
    public required string FleaBannedColour { get; set; }
}