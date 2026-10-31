// CotlDataDump: dumps items, structures and skins from Cult of the Lamb for pack.py.
//
// Phase 1, main menu, before any save loads: costs (GetCost applies the loaded save's Mine II discount),
// item/structure data, skins, translations. Phase 2: continues save slot 0 and, in the base, reads
// footprints and icons, which only exist there. Saving is blocked the whole time, and the process is
// killed at the end. Output: BepInEx/plugins/CotlDataDump/ (raw dump format in docs/design.md).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using I2.Loc;
using Lamb.UI.Assets;
using MMTools;
using Newtonsoft.Json;
using Spine.Unity;
using src.Managers;
using UnityEngine;
using UnityEngine.SceneManagement;
using Dict = System.Collections.Generic.Dictionary<string, object>;

[BepInPlugin("chrsnv.cotldata.dump", "COTL Data Dump", "0.1.0")]
public class CotlDataDump : BaseUnityPlugin
{
    ConfigEntry<int> slot;
    ConfigEntry<bool> doItems, doStructures, doSkins, doRituals, doMore;
    string outDir;
    bool started, finished;
    readonly List<object> errors = new List<object>();
    readonly HashSet<string> terms = new HashSet<string>();
    readonly Dictionary<string, Dictionary<string, string>> i18n = new Dictionary<string, Dictionary<string, string>>();
    List<Dictionary<string, object>> items, structures, skins, rituals;
    readonly Dictionary<string, List<string>> unnamed = new Dictionary<string, List<string>>();
    readonly Dictionary<string, string> skinNames = new Dictionary<string, string>();
    string en = "en";

    void Awake()
    {
        // One-shot and short-lived: publish.sh arms it with the time; a cfg left behind by a failed cleanup must not
        // take over normal play later.
        var armedAt = Config.Bind("General", "ArmedAt", 0L, "Unix time publish.sh armed this plugin; it acts only within 30 minutes of it, once.");
        long age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - armedAt.Value;
        if (armedAt.Value == 0 || age < 0 || age > 30 * 60) { Logger.LogInfo("not armed; doing nothing"); return; }
        armedAt.Value = 0;
        Config.Save();
        slot = Config.Bind("General", "Slot", 0, "Save slot to continue for phase 2 (footprints and icons). Never written to.");
        doItems = Config.Bind("Jobs", "Items", true, "items.json and item icons.");
        doStructures = Config.Bind("Jobs", "Structures", true, "structures.json and structure icons.");
        doSkins = Config.Bind("Jobs", "Skins", true, "skins.json.");
        doRituals = Config.Bind("Jobs", "Rituals", true, "rituals.json and ritual icons.");
        doMore = Config.Bind("Jobs", "More", true, "doctrines, sermons, tarot, relics, weapons, curses, traits, outfits, crown abilities, fleeces.");
        outDir = Path.Combine(Paths.PluginPath, "CotlDataDump");
        Directory.CreateDirectory(outDir);
        new Harmony("chrsnv.cotldata.dump").PatchAll(typeof(CotlDataDump));
        SceneManager.sceneLoaded += OnSceneLoaded;
        Logger.LogInfo("loaded; saving is blocked while this plugin is installed");
    }

    [HarmonyPrefix, HarmonyPatch(typeof(SaveAndLoad), "Saving")] static bool NoSave() => false;
    [HarmonyPrefix, HarmonyPatch(typeof(SaveAndLoad), nameof(SaveAndLoad.SaveMetaData))] static bool NoMeta() => false;
    [HarmonyPrefix, HarmonyPatch(typeof(PersistenceManager), nameof(PersistenceManager.Save))] static bool NoPersist() => false;

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Logger.LogInfo($"scene loaded: {scene.name}");
        if (scene.name == "Main Menu" && !started) { started = true; StartCoroutine(MainMenu()); }
        if (scene.name == "Base Biome 1" && started && !finished) StartCoroutine(Base());
    }

    IEnumerator MainMenu()
    {
        yield return new WaitForSecondsRealtime(8f);
        // A loaded save would change costs; DataManager must still be the fresh one the game makes on demand.
        if (UpgradeSystem.GetUnlocked(UpgradeSystem.Type.Economy_MineII)) Fail("costs", "a save's upgrades are already loaded");
        var english = LocalizationManager.Sources.SelectMany(s => s.mLanguages).FirstOrDefault(l => l.Name == "English");
        if (english != null && !string.IsNullOrEmpty(english.Code)) en = english.Code;
        if (doItems.Value) Try("Items", ReadItems);
        if (doStructures.Value) Try("Structures", ReadStructures);
        if (doSkins.Value) Try("Skins", ReadSkins);
        if (doRituals.Value) Try("Rituals", ReadRituals);
        if (doMore.Value)
        {
            Try("Doctrines", ReadDoctrines); Try("Tarot", ReadTarot); Try("Relics", ReadRelics); Try("Weapons", ReadWeapons);
            Try("Traits", ReadTraits); Try("Outfits", ReadOutfits); Try("CrownAbilities", ReadCrownAbilities);
            Try("SaveSchema", ReadSaveSchema);
        }
        int s = slot.Value;
        Logger.LogInfo($"phase 1 done; continuing save slot {s}");
        SaveAndLoad.SAVE_SLOT = s;
        // same as the menu's Continue (LoadMenu.ContinueGame)
        MMTransition.Play(MMTransition.TransitionType.ChangeRoomWaitToResume, MMTransition.Effect.BlackFade, "Base Biome 1", 3f, "", () => SaveAndLoad.Load(s));
    }

    IEnumerator Base()
    {
        float waited = 0f;
        while (!SaveAndLoad.Loaded && waited < 180f) { waited += Time.unscaledDeltaTime; yield return null; }
        yield return new WaitForSecondsRealtime(5f);
        DataManager.Instance.DisableSaving = true;
        if (doItems.Value) Try("ItemIcons", ItemIcons);
        if (doRituals.Value) Try("RitualIcons", RitualIcons);
        if (doMore.Value)
        {
            yield return DoctrineIcons();
            Try("TraitIcons", TraitIcons); Try("CrownIcons", CrownIcons); Try("Fleeces", ReadFleeces); Try("LaterIcons", LaterIcons);
            yield return Portraits();
        }
        // last: phase 2 adds terms too (fleece names)
        Try("Translations", ReadTranslations);
        if (doStructures.Value) Try("StructureFootprints", StructureFootprints);
        finished = true;
        Write("items.json", items ?? new List<Dictionary<string, object>>());
        Write("structures.json", structures ?? new List<Dictionary<string, object>>());
        Write("skins.json", skins ?? new List<Dictionary<string, object>>());
        Write("rituals.json", rituals ?? new List<Dictionary<string, object>>());
        foreach (var kind in new[] { "doctrines", "sermons", "tarot", "relics", "weapons", "curses", "traits", "outfits", "crownAbilities", "fleeces" })
            Write($"{kind}.json", more.TryGetValue(kind, out var l) ? l : new List<Dict>());
        Write("i18n.json", i18n);
        Write("saveSchema.json", saveSchema);
        Write("meta.json", new Dictionary<string, object> {
            ["gameVersion"] = Application.version, ["pluginVersion"] = Info.Metadata.Version.ToString(),
            ["capturedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["steamBuildId"] = SteamBuildId(), ["errors"] = errors,
            ["unnamedItems"] = unnamed });
        File.WriteAllText(Path.Combine(outDir, "done"), "");
        Logger.LogInfo("dump done");
        yield return new WaitForSecondsRealtime(1f);
        // Quit is swallowed under Proton; saving is blocked, so ending the process loses nothing.
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }

    // ---------- phase 1 ----------

    // The term's English text from the sources themselves (GetTranslation returns text for missing terms too).
    static string English(string term)
    {
        foreach (var source in LocalizationManager.Sources)
        {
            var data = source.GetTermData(term);
            int i = source.mLanguages.FindIndex(l => l.Name == "English");
            if (data != null && i >= 0 && i < data.Languages.Length && !string.IsNullOrEmpty(data.Languages[i])) return data.Languages[i];
        }
        return null;
    }

    // Enum.GetNames, and the name carried through: an alias would otherwise come back under its value's first name.
    // Types without an English name are internal.
    static IEnumerable<(string name, T value)> Named<T>(string prefix) =>
        Enum.GetNames(typeof(T)).Where(n => English($"{prefix}/{n}") != null).Select(n => (n, (T)Enum.Parse(typeof(T), n)));

    string Term(string term)
    {
        if (English(term) == null) return null;
        terms.Add(term);
        return term;
    }

    static readonly Dictionary<string, Func<InventoryItem.ITEM_TYPE, bool>> ItemTags = new Dictionary<string, Func<InventoryItem.ITEM_TYPE, bool>> {
        ["food"] = InventoryItem.IsFood, ["fish"] = InventoryItem.IsFish, ["big_fish"] = InventoryItem.IsBigFish,
        ["meat"] = InventoryItem.IsMeat, ["heart"] = InventoryItem.IsHeart, ["gift"] = InventoryItem.IsGiftOrNecklace,
    };

    // Most items are named under Inventory/{id}; meals and drinks under CookingData/{id}/Name (CookingData.GetLocalizedName).
    string ItemTerm(string id, string inventory, string cooking) =>
        English($"Inventory/{id}") != null ? Term($"Inventory/{id}{inventory}") : Term($"CookingData/{id}{cooking}");

    // A meal's or drink's recipes, numbers and effects, all as the cooking code defines them (none read the save).
    Dictionary<string, object> Meal(InventoryItem.ITEM_TYPE t) => new Dictionary<string, object> {
        ["recipes"] = (CookingData.GetRecipe(t) ?? new List<List<InventoryItem>>()).Select(r => r.Select(i => new Dictionary<string, object> {
            ["item"] = ((InventoryItem.ITEM_TYPE)i.type).ToString(), ["quantity"] = i.quantity }).ToList()).ToList(),
        ["satiation"] = CookingData.GetSatationAmount(t), ["satiationPlayer"] = CookingData.GetSatationAmountPlayer(t),
        ["stars"] = CookingData.GetSatationLevel(t), ["tummyRating"] = CookingData.GetTummyRating(t),
        ["faith"] = CookingData.GetFaithAmount(t), ["cookTime"] = CookingData.GetMealCookDuration(t), ["dlc"] = CookingData.IsRecipeDLC(t),
        ["effects"] = (CookingData.GetMealEffects(t) ?? new CookingData.MealEffect[0]).Select(e => new Dictionary<string, object> {
            ["type"] = e.MealEffectType.ToString(), ["chance"] = e.Chance,
            ["descTerm"] = Term($"CookingData/{e.MealEffectType}/Description") ?? $"CookingData/{e.MealEffectType}/Description" }).ToList(),
    };

    // How a farm seed grows, from CropController; Structures_FarmerPlot.GrowsDuringWinter: only snow fruit grows in winter,
    // and only then.
    static Dictionary<string, object> Crop(InventoryItem.ITEM_TYPE seed, string grows)
    {
        var harvests = CropController.GetHarvestsPerSeedRange(seed);
        return new Dictionary<string, object> {
            ["grows"] = grows, ["growthTime"] = CropController.CropGrowthTimes(seed), ["stages"] = CropController.CropStatesForSeedType(seed),
            ["harvests"] = new Dictionary<string, int> { ["min"] = harvests.x, ["max"] = harvests.y },
            ["season"] = seed == InventoryItem.ITEM_TYPE.SEED_SNOW_FRUIT ? "winter" : "spring",
        };
    }

    void ReadItems()
    {
        var drinks = new HashSet<InventoryItem.ITEM_TYPE>(CookingData.GetAllDrinks());
        var farmSeeds = new HashSet<InventoryItem.ITEM_TYPE>(InventoryItem.AllSeeds);
        var grows = new Dictionary<InventoryItem.ITEM_TYPE, InventoryItem.ITEM_TYPE>();  // seed -> crop, the reverse of GetSeedType
        foreach (InventoryItem.ITEM_TYPE crop in Enum.GetValues(typeof(InventoryItem.ITEM_TYPE)))
            if (InventoryItem.GetSeedType(crop) != InventoryItem.ITEM_TYPE.NONE && !grows.ContainsKey(InventoryItem.GetSeedType(crop)))
                grows[InventoryItem.GetSeedType(crop)] = crop;
        items = new List<Dictionary<string, object>>();
        foreach (var id in Enum.GetNames(typeof(InventoryItem.ITEM_TYPE)))
        {
            if (English($"Inventory/{id}") == null && English($"CookingData/{id}/Name") == null) { unnamed[id] = null; continue; }
            var t = (InventoryItem.ITEM_TYPE)Enum.Parse(typeof(InventoryItem.ITEM_TYPE), id);
            bool cooked = English($"Inventory/{id}") == null;  // named under CookingData: a meal or a drink
            items.Add(new Dictionary<string, object> {
                ["id"] = id, ["nameTerm"] = ItemTerm(id, "", "/Name"), ["descTerm"] = ItemTerm(id, "/Description", "/Description"),
                ["category"] = InventoryItem.GetItemCategory(t).ToString(),
                ["tags"] = ItemTags.Where(tag => tag.Value(t)).Select(tag => tag.Key).Concat(cooked ? new[] { drinks.Contains(t) ? "drink" : "meal" } : new string[0]).ToList(),
                ["meal"] = cooked ? Meal(t) : null,
                ["seed"] = InventoryItem.GetSeedType(t) == InventoryItem.ITEM_TYPE.NONE ? null : InventoryItem.GetSeedType(t).ToString(),
                ["crop"] = farmSeeds.Contains(t) ? Crop(t, grows.TryGetValue(t, out var c) ? c.ToString() : null) : null,
            });
        }
        // For the types left out, any term in the game that names them some other way, as evidence they're unnamed.
        foreach (var id in unnamed.Keys.ToList())
            unnamed[id] = LocalizationManager.Sources.SelectMany(src => src.mTerms).Select(d => d.Term)
                .Where(term => term.EndsWith("/" + id) || term.Contains("/" + id + "/")).Distinct().ToList();
        Logger.LogInfo($"items: {items.Count}, left out: {unnamed.Count}");
    }

    // The build menu's tabs and sections, as its own category classes list them (StructuresData.AllStructures misses some).
    static Dictionary<StructureBrain.TYPES, string> BuildMenu()
    {
        var sections = new List<(string, IEnumerable<StructureBrain.TYPES>)>();
        foreach (Lamb.UI.BuildMenu.FollowerCategory.Category c in Enum.GetValues(typeof(Lamb.UI.BuildMenu.FollowerCategory.Category)))
            sections.Add(($"Follower/{c}", Lamb.UI.BuildMenu.FollowerCategory.GetStructuresForCategory(c)));
        sections.Add(("Faith/General", Lamb.UI.BuildMenu.FaithCategory.AllStructures()));
        sections.Add(("Faith/Sin", Lamb.UI.BuildMenu.FaithCategory.SinStructures()));
        foreach (Lamb.UI.BuildMenu.MajorDLCCategory.Category c in Enum.GetValues(typeof(Lamb.UI.BuildMenu.MajorDLCCategory.Category)))
            sections.Add(($"MajorDLC/{c}", Lamb.UI.BuildMenu.MajorDLCCategory.GetStructuresForCategory(c)));
        foreach (DataManager.DecorationType d in Enum.GetValues(typeof(DataManager.DecorationType)))
            sections.Add(($"Aesthetic/{d}", DataManager.DecorationsForType(d)));
        var menu = new Dictionary<StructureBrain.TYPES, string>();
        foreach (var (name, types) in sections)
            foreach (var t in types ?? Enumerable.Empty<StructureBrain.TYPES>())
                if (!menu.ContainsKey(t)) menu[t] = name;
        return menu;
    }

    void ReadStructures()
    {
        var menu = BuildMenu();
        structures = Named<StructureBrain.TYPES>("Structures").Select(named =>
        {
            var (id, t) = named;
            StructuresData info = null;
            try { info = StructuresData.GetInfoByType(t, 0); } catch { }
            return new Dictionary<string, object> {
                ["id"] = id, ["nameTerm"] = Term($"Structures/{id}"), ["descTerm"] = Term($"Structures/{id}/Description"),
                ["cost"] = StructuresData.GetCost(t).Select(c => new Dictionary<string, object> { ["item"] = c.CostItem.ToString(), ["quantity"] = c.CostValue }).ToList(),
                ["upgradesFrom"] = info != null && info.IsUpgrade ? info.UpgradeFromType.ToString() : null,
                ["size"] = null,
                ["menu"] = menu.TryGetValue(t, out var m) ? m : null,
                ["buildable"] = menu.ContainsKey(t),
                // GetCategory falls back to TECH for anything it doesn't list, so only buildable types get one.
                ["category"] = menu.ContainsKey(t) ? StructuresData.GetCategory(t).ToString() : null,
            };
        }).ToList();
        Logger.LogInfo($"structures: {structures.Count}");
    }

    void ReadSkins()
    {
        skins = new List<Dictionary<string, object>>();
        // Invariant forms too (bosses, cult leaders): saves use them, and the editor needs their portraits.
        foreach (var c in WorshipperData.Instance.Characters.Where(c => c.Skin.Count > 0))
        {
            string id = new string(c.Skin[0].Skin.Select(ch => char.IsLetterOrDigit(ch) && ch < 128 || ch == '_' || ch == '-' ? ch : '_').ToArray());
            // Some forms are listed twice (e.g. for two drop locations); the first entry is the one the game finds.
            if (skinNames.TryGetValue(id, out var first))
            {
                // the same form listed twice is skipped; two forms whose names clean up to one id would lose one
                if (first == c.Skin[0].Skin || c.Invariant) { Logger.LogWarning($"skin {id} listed again as {c.Title}, skipped"); continue; }
                Fail("Skins", $"\"{first}\" and \"{c.Skin[0].Skin}\" both become id {id}");
                continue;
            }
            skinNames[id] = c.Skin[0].Skin;
            // The game has no translated skin names, only this title.
            i18n[$"Skins/{id}"] = new Dictionary<string, string> { [en] = string.IsNullOrEmpty(c.Title) ? c.Skin[0].Skin : c.Title };
            skinSources[id] = c;
            skins.Add(new Dictionary<string, object> {
                ["id"] = id, ["nameTerm"] = $"Skins/{id}", ["unlock"] = c.DropLocation.ToString(), ["hidden"] = c.Hidden,
                ["invariant"] = c.Invariant, ["variants"] = c.Skin.Select(s => s.Skin).Distinct().ToList(),
            });
        }
        Logger.LogInfo($"skins: {skins.Count}");
    }

    // The ritual menu's list (UpgradeSystem.AllRituals) and how each ritual is unlocked; nothing here reads the save,
    // except the faith change, which only differs once the cult has the matching trait.
    void ReadRituals()
    {
        var groups = new Dictionary<UpgradeSystem.Type, string>();
        foreach (var r in UpgradeSystem.SecondaryRituals) groups[r] = "secondary";
        foreach (var r in UpgradeSystem.SecondaryRitualPairs) groups[r] = "pair";
        foreach (var r in UpgradeSystem.SingleRituals) groups[r] = "single";
        foreach (var r in UpgradeSystem.SpecialRituals) groups[r] = "special";
        var pairs = UpgradeSystem.SecondaryRitualPairs;  // (0, 1), (2, 3), ...: the two choices of one doctrine
        var doctrines = new Dictionary<UpgradeSystem.Type, string>();
        foreach (DoctrineUpgradeSystem.DoctrineType d in Enum.GetValues(typeof(DoctrineUpgradeSystem.DoctrineType)))
            try { var r = DoctrineUpgradeSystem.RitualForDoctrineUpgrade(d); if (groups.ContainsKey(r) && !doctrines.ContainsKey(r)) doctrines[r] = d.ToString(); } catch { }
        // UnlockAbility(Winter_RanchMeat) unlocks it, but RitualForDoctrineUpgrade leaves it out
        if (!doctrines.ContainsKey(UpgradeSystem.Type.Ritual_RanchMeat)) doctrines[UpgradeSystem.Type.Ritual_RanchMeat] = "Winter_RanchMeat";
        rituals = new List<Dictionary<string, object>>();
        foreach (var r in UpgradeSystem.AllRituals())
        {
            int i = Array.IndexOf(pairs, r);
            var trait = UpgradeSystem.GetRitualTrait(r);
            rituals.Add(new Dictionary<string, object> {
                ["id"] = r.ToString(),
                // UpgradeSystem.GetLocalizedName names the fire pit after its building
                ["nameTerm"] = Term(r == UpgradeSystem.Type.Ritual_FirePit ? "UpgradeSystem/Building_DancingFirepit/Name" : $"UpgradeSystem/{r}/Name"),
                ["descTerm"] = Term($"UpgradeSystem/{r}/Description"),
                ["cost"] = UpgradeSystem.GetCost(r).Select(c => new Dictionary<string, object> { ["item"] = c.CostItem.ToString(), ["quantity"] = c.CostValue }).ToList(),
                ["faith"] = UpgradeSystem.GetRitualFaithChange(r), ["warmth"] = UpgradeSystem.GetRitualWarmthChange(r), ["sin"] = UpgradeSystem.GetRitualSinChange(r),
                ["trait"] = trait == FollowerTrait.TraitType.None ? null : trait.ToString(),
                ["group"] = groups[r],
                ["alternative"] = i < 0 ? null : pairs[i ^ 1].ToString(),
                ["doctrine"] = doctrines.TryGetValue(r, out var doc) ? doc : null,
            });
        }
        Logger.LogInfo($"rituals: {rituals.Count}");
    }

    // The build of the game being captured, so a watcher can tell when the API falls behind (0 if Steam isn't up).
    static uint SteamBuildId()
    {
        try { return SteamManager.Initialized ? (uint)Steamworks.SteamApps.GetAppBuildId() : 0; } catch { return 0; }
    }

    // ---------- more kinds (each list is what the game's own UI iterates) ----------

    readonly Dictionary<string, List<Dict>> more = new Dictionary<string, List<Dict>>();
    readonly Dict saveSchema = new Dict();

    static readonly Dictionary<Type, string> Primitives = new Dictionary<Type, string> {
        [typeof(bool)] = "bool", [typeof(sbyte)] = "int8", [typeof(byte)] = "uint8", [typeof(short)] = "int16",
        [typeof(ushort)] = "uint16", [typeof(int)] = "int32", [typeof(uint)] = "uint32", [typeof(long)] = "int64",
        [typeof(ulong)] = "uint64", [typeof(float)] = "float32", [typeof(double)] = "float64", [typeof(string)] = "string",
        [typeof(byte[])] = "bytes" };

    // A 1.5+ save is MessagePack-CSharp arrays without names: each [Key(n)] member of DataManager (slots) and MetaData
    // (meta files), and of every [MessagePackObject] type they reach, with its key and type.
    void ReadSaveSchema()
    {
        var types = new Dict();
        var enums = new Dict();
        var queue = new Queue<Type>(new[] { typeof(DataManager), typeof(MetaData) });
        var seen = new HashSet<Type>(queue);
        string Name(Type t) => t.FullName.Replace('+', '.');
        object Describe(Type t)
        {
            if (Primitives.TryGetValue(t, out var p)) return p;
            var inner = Nullable.GetUnderlyingType(t);
            if (inner != null) return new Dict { ["nullable"] = Describe(inner) };
            if (t.IsEnum)
            {
                if (!enums.ContainsKey(Name(t)))
                    enums[Name(t)] = Enum.GetValues(t).Cast<object>()
                        .GroupBy(v => Convert.ChangeType(v, Enum.GetUnderlyingType(t)).ToString())
                        .ToDictionary(g => g.Key, g => (object)g.First().ToString());
                return new Dict { ["enum"] = Name(t) };
            }
            if (t.IsArray) return new Dict { ["list"] = Describe(t.GetElementType()) };
            if (t.IsGenericType)
            {
                var g = t.GetGenericTypeDefinition();
                var a = t.GetGenericArguments();
                if (g == typeof(List<>) || g == typeof(HashSet<>)) return new Dict { ["list"] = Describe(a[0]) };
                if (g == typeof(Dictionary<,>)) return new Dict { ["map"] = new List<object> { Describe(a[0]), Describe(a[1]) } };
            }
            if (t.IsDefined(typeof(MessagePack.MessagePackObjectAttribute), false))
            {
                if (seen.Add(t)) queue.Enqueue(t);
                return new Dict { ["object"] = Name(t) };
            }
            return new Dict { ["other"] = Name(t) };
        }
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            var fields = new List<Dict>();
            // DeclaredOnly per class up the chain: GetFields alone skips a base class's private members.
            var declared = new List<MemberInfo>();
            for (var c = t; c != null && c != typeof(object); c = c.BaseType)
                declared.AddRange(c.GetFields(members | BindingFlags.DeclaredOnly).Cast<MemberInfo>().Concat(c.GetProperties(members | BindingFlags.DeclaredOnly)));
            foreach (var m in declared)
            {
                var key = m.GetCustomAttributes(typeof(MessagePack.KeyAttribute), true).Cast<MessagePack.KeyAttribute>().FirstOrDefault();
                if (key?.IntKey == null) continue;
                var type = m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;
                fields.Add(new Dict { ["key"] = key.IntKey.Value, ["name"] = m.Name, ["type"] = Describe(type) });
            }
            types[Name(t)] = new Dict { ["fields"] = fields.OrderBy(x => (int)x["key"]).ToList() };
        }
        saveSchema["roots"] = new Dict { ["slot"] = Name(typeof(DataManager)), ["meta"] = Name(typeof(MetaData)) };
        saveSchema["types"] = types;
        saveSchema["enums"] = enums;
        Logger.LogInfo($"save schema: {types.Count} types, {enums.Count} enums");
    }
    static readonly string[] SermonCategories = { "WorkAndWorship", "Possession", "Food", "Afterlife", "LawAndOrder", "Pleasure", "Winter" };

    // Doctrines as the temple offers them: GetSermonReward(category, level 1-4, first or second choice), plus the nine
    // Special ones Death Cat and the mystic shop give. Sermon categories are the seven with names.
    void ReadDoctrines()
    {
        var rituals = new HashSet<UpgradeSystem.Type>(UpgradeSystem.AllRituals());
        var list = new List<Dict>();
        var seen = new HashSet<DoctrineUpgradeSystem.DoctrineType>();
        void Add(DoctrineUpgradeSystem.DoctrineType t, string category, int level, string choice, string other)
        {
            if (!seen.Add(t)) return;
            var kind = DoctrineUpgradeSystem.ShowDoctrineTutorialForType(t).ToString();
            var r = DoctrineUpgradeSystem.RitualForDoctrineUpgrade(t);
            // UnlockAbility(Winter_RanchMeat) unlocks Gorging, but RitualForDoctrineUpgrade leaves it out
            if (t.ToString() == "Winter_RanchMeat") r = UpgradeSystem.Type.Ritual_RanchMeat;
            list.Add(new Dict {
                ["id"] = t.ToString(), ["nameTerm"] = Term($"DoctrineUpgradeSystem/{t}"), ["descTerm"] = Term($"DoctrineUpgradeSystem/{t}/Description"),
                ["category"] = category, ["level"] = level, ["choice"] = choice, ["alternative"] = other,
                ["kind"] = kind == "None" ? null : kind, ["ritual"] = rituals.Contains(r) ? r.ToString() : null,
            });
        }
        var sermons = new List<Dict>();
        foreach (var name in SermonCategories)
        {
            var cat = (SermonCategory)Enum.Parse(typeof(SermonCategory), name);
            for (int level = 1; level <= 4; level++)
            {
                var a = DoctrineUpgradeSystem.GetSermonReward(cat, level, true);
                var b = DoctrineUpgradeSystem.GetSermonReward(cat, level, false);
                Add(a, name, level, "first", b.ToString());
                Add(b, name, level, "second", a.ToString());
            }
            sermons.Add(new Dict { ["id"] = name, ["nameTerm"] = Term($"DoctrineUpgradeSystem/{name}"),
                ["descTerm"] = Term($"DoctrineUpgradeSystem/{name}/Description"), ["glyph"] = DoctrineUpgradeSystem.GetSermonCategoryIcon(cat) });
        }
        for (int level = 1; level <= 9; level++)
            Add(DoctrineUpgradeSystem.GetSermonReward(SermonCategory.Special, level, true), "Special", level, null, null);
        more["doctrines"] = list.Where(d => d["nameTerm"] != null).ToList();
        more["sermons"] = sermons;
        Logger.LogInfo($"doctrines: {more["doctrines"].Count}, sermons: {sermons.Count}");
    }

    // DataManager.AllTrinkets is the deck the game draws from. Text per upgrade level is .../Description{n}; corrupted
    // cards fill {0} and {1} with .../Positive and .../Negative (what LocalisedDescription does).
    void ReadTarot()
    {
        var groups = new (string, IEnumerable<TarotCards.Card>)[] {
            ("default", TarotCards.DefaultCards), ("coop", TarotCards.CoopCards), ("dlc", TarotCards.DLCWithBlueBack),
            ("majorDlc", TarotCards.MajorDLCCards), ("corrupted", TarotCards.CorruptedCards), ("mystic", TarotCards.MysticCards),
            ("rot", TarotCards.RotCards), ("winter", TarotCards.WitnerCards) };
        more["tarot"] = DataManager.AllTrinkets.Distinct().Select(c =>
        {
            string id = c.ToString(), desc = $"TarotCards/{id}/Description";
            int max = TarotCards.GetMaxTarotCardLevel(c);
            return new Dict {
                ["id"] = id, ["nameTerm"] = Term($"TarotCards/{id}/Name"), ["descTerm"] = Term(desc),
                ["descArgs"] = English($"TarotCards/{id}/Positive") == null ? null : new List<string> { Term($"TarotCards/{id}/Positive"), Term($"TarotCards/{id}/Negative") },
                ["levels"] = Enumerable.Range(1, max).Select(n => new Dict { ["descTerm"] = Term(desc + n) ?? Term(desc) }).ToList(),
                ["loreTerm"] = Term($"TarotCards/{id}/Lore"),
                ["maxLevel"] = max, ["groups"] = groups.Where(g => g.Item2.Contains(c)).Select(g => g.Item1).ToList(),
                ["dlc"] = TarotCards.GetTarotIsDLC(c), ["coop"] = TarotCards.GetTarotIsCoOp(c), ["weight"] = TarotCards.GetTarotCardWeight(c),
            };
        }).Where(d => d["nameTerm"] != null).ToList();
        Logger.LogInfo($"tarot: {more["tarot"].Count}");
    }

    // EquipmentManager.RelicData (Resources "Data/Equipment Data/Relics"), as the relic menu lists them. Icons are the
    // fields themselves: the Sprite getters read the save.
    void ReadRelics()
    {
        more["relics"] = new List<Dict>();
        foreach (var rd in EquipmentManager.RelicData.Where(r => r && r.RelicType != RelicType.None).GroupBy(r => r.RelicType).Select(g => g.First()))
        {
            string id = rd.RelicType.ToString();
            if (English($"Relics/{id}") == null) continue;
            bool corrupted = rd.RelicSubType.ToString() == "Corrupted" && English($"Relics/{id}/Positive") != null;
            more["relics"].Add(new Dict {
                ["id"] = id, ["nameTerm"] = Term($"Relics/{id}"), ["descTerm"] = Term($"Relics/{id}/Description"),
                ["descArgs"] = corrupted ? new List<string> { Term($"Relics/{id}/Positive"), Term($"Relics/{id}/Negative") } : null,
                ["loreTerm"] = Term($"Relics/{id}/Lore"),
                ["subType"] = rd.RelicSubType.ToString(), ["interactionType"] = rd.InteractionType.ToString(), ["damageType"] = rd.DamageType.ToString(),
                ["chargeDamage"] = rd.DamageRequiredToCharge, ["chargeCategory"] = RelicData.GetChargeCategory(rd).ToString(), ["weight"] = rd.Weight,
                ["requiresCult"] = rd.RequiresCult, ["unlockedBy"] = rd.UpgradeType == UpgradeSystem.Type.Count ? null : rd.UpgradeType.ToString(),
                ["dlc"] = RelicData.GetRelicDLC(rd.RelicType), ["coop"] = EquipmentManager.CoopRelics.Contains(rd.RelicType),
                ["majorDlc"] = EquipmentManager.MajorDLCRelics.Contains(rd.RelicType),
            });
            IconNowOrLater(rd.UISprite, "relics", id);
        }
        Logger.LogInfo($"relics: {more["relics"].Count}");
    }

    // The Resources folders EquipmentManager loads; types without a name (shields, the jailers' chains) aren't the player's.
    void ReadWeapons()
    {
        more["weapons"] = new List<Dict>();
        more["curses"] = new List<Dict>();
        foreach (var w in Resources.LoadAll<WeaponData>("Data/Equipment Data/Weapons").Where(w => w).GroupBy(w => w.EquipmentType).Select(g => g.First()))
        {
            string id = w.EquipmentType.ToString();
            if (English($"UpgradeSystem/{id}/Name") == null) continue;
            more["weapons"].Add(new Dict {
                ["id"] = id, ["nameTerm"] = Term($"UpgradeSystem/{id}/Name"), ["descTerm"] = Term($"UpgradeSystem/{id}/Description"), ["loreTerm"] = Term($"UpgradeSystem/{id}/Lore"),
                ["family"] = w.PrimaryEquipmentType.ToString(), ["legendary"] = EquipmentManager.LegendaryWeapons.Contains(w.EquipmentType),
                ["speed"] = w.Speed, ["damage"] = (w.Combos ?? new List<PlayerWeapon.WeaponCombos>()).Select(c => c.Damage).ToList(),
                ["heavyDamage"] = w.HeavyAttackCombo?.Damage,
            });
            IconNowOrLater(w.UISprite, "weapons", id);
        }
        foreach (var c in Resources.LoadAll<CurseData>("Data/Equipment Data/Curses").Where(c => c).GroupBy(c => c.EquipmentType).Select(g => g.First()))
        {
            string id = c.EquipmentType.ToString();
            if (English($"UpgradeSystem/{id}/Name") == null) continue;
            more["curses"].Add(new Dict {
                ["id"] = id, ["nameTerm"] = Term($"UpgradeSystem/{id}/Name"), ["descTerm"] = Term($"UpgradeSystem/{id}/Description"), ["loreTerm"] = Term($"UpgradeSystem/{id}/Lore"),
                ["family"] = c.PrimaryEquipmentType.ToString(), ["damage"] = c.Damage, ["delay"] = c.Delay,
                ["castingDuration"] = c.CastingDuration, ["chance"] = c.Chance, ["canAim"] = c.CanAim,
            });
            IconNowOrLater(c.UISprite, "curses", id);
        }
        Logger.LogInfo($"weapons: {more["weapons"].Count}, curses: {more["curses"].Count}");
    }

    void ReadTraits()
    {
        var groups = new (string, List<FollowerTrait.TraitType>)[] {
            ("starting", FollowerTrait.StartingTraits), ("rareStarting", FollowerTrait.RareStartingTraits), ("single", FollowerTrait.SingleTraits),
            ("faithful", FollowerTrait.FaithfulTraits), ("good", FollowerTrait.GoodTraits), ("unique", FollowerTrait.UniqueTraits),
            ("sin", FollowerTrait.SinTraits), ("majorDlc", FollowerTrait.MajorDLCTraits), ("winter", FollowerTrait.WinterSpecificTraits),
            ("pureBlood", FollowerTrait.PureBloodTraits), ("excludedFromMating", FollowerTrait.ExcludedFromMating) };
        more["traits"] = new List<Dict>();
        foreach (var id in Enum.GetNames(typeof(FollowerTrait.TraitType)))
        {
            if (id == "None" || English($"Traits/{id}") == null) continue;
            var t = (FollowerTrait.TraitType)Enum.Parse(typeof(FollowerTrait.TraitType), id);
            string other = FollowerTrait.ExclusiveTraits.TryGetValue(t, out var x) ? x.ToString()
                : FollowerTrait.ExclusiveTraits.Where(p => p.Value == t).Select(p => p.Key.ToString()).FirstOrDefault();
            more["traits"].Add(new Dict {
                ["id"] = id, ["nameTerm"] = Term($"Traits/{id}"), ["descTerm"] = Term($"Traits/{id}/Description"),
                ["positive"] = FollowerTrait.IsPositiveTrait(t), ["groups"] = groups.Where(g => g.Item2.Contains(t)).Select(g => g.Item1).ToList(),
                ["exclusiveWith"] = other,
            });
        }
        Logger.LogInfo($"traits: {more["traits"].Count}");
    }

    // TailorManager.ClothingData (Resources), minus what the tailor menu hides; tabs as TailorMenuConfigure builds them.
    void ReadOutfits()
    {
        more["outfits"] = new List<Dict>();
        foreach (var cd in TailorManager.ClothingData.Where(c => c && !c.HideOnTailorMenu).GroupBy(c => c.ClothingType).Select(g => g.First()))
        {
            string id = cd.ClothingType.ToString(), protection = cd.ProtectionType.ToString();
            if (id == "None" || id == "Count") continue;
            if (English($"Clothing/{id}/Name") == null) continue;
            var groups = new List<string>();
            if (cd.IsHeretic) groups.Add("heretic"); if (cd.IsCultist) groups.Add("cultist");
            if (cd.IsSinful) groups.Add("sinful"); if (cd.IsPilgrim) groups.Add("pilgrim"); if (cd.IsSecret) groups.Add("secret");
            more["outfits"].Add(new Dict {
                ["id"] = id, ["nameTerm"] = Term($"Clothing/{id}/Name"), ["descTerm"] = Term($"Clothing/{id}/Description"),
                ["textTerms"] = new Dict { ["requirements"] = Term($"Clothing/{id}/Requirements"),
                    ["protection"] = protection == "None" ? null : Term($"Clothing/FollowerProtectionType/{protection}") },
                ["cost"] = (cd.Cost ?? new ClothingData.CostItem[0]).Select(c => new Dict { ["item"] = c.ItemType.ToString(), ["quantity"] = c.Cost }).ToList(),
                ["effects"] = (cd.Effects ?? new CookingData.MealEffect[0]).Select(e => new Dict {
                    ["type"] = e.MealEffectType.ToString(), ["chance"] = e.Chance,
                    ["descTerm"] = Term($"CookingData/{e.MealEffectType}/Description") ?? $"CookingData/{e.MealEffectType}/Description" }).ToList(),
                ["tab"] = cd.SpecialClothing ? "special" : cd.IsMajorDLC ? "majorDlc" : cd.IsDLC ? "dlc" : "default",
                ["groups"] = groups, ["forSale"] = cd.ForSale,
            });
        }
        Logger.LogInfo($"outfits: {more["outfits"].Count}");
    }

    // What UIPlayerUpgradesMenuController sells for monster hearts (_crownUpgrades, _crownDLCUpgrades); CrownAbilities.TYPE
    // is older and unused.
    static readonly UpgradeSystem.Type[] CrownUpgrades = { UpgradeSystem.Type.Ability_Resurrection, UpgradeSystem.Type.Ability_Eat,
        UpgradeSystem.Type.Ability_TeleportHome, UpgradeSystem.Type.Ability_BlackHeart, UpgradeSystem.Type.Ability_WinterChoice };

    void ReadCrownAbilities()
    {
        more["crownAbilities"] = CrownUpgrades.Select(t => new Dict {
            ["id"] = t.ToString(), ["nameTerm"] = Term($"UpgradeSystem/{t}/Name"), ["descTerm"] = Term($"UpgradeSystem/{t}/Description"),
            ["cost"] = UpgradeSystem.GetCost(t).Select(c => new Dict { ["item"] = c.CostItem.ToString(), ["quantity"] = c.CostValue }).ToList(),
            ["dlc"] = t == UpgradeSystem.Type.Ability_WinterChoice,
        }).Where(d => d["nameTerm"] != null).ToList();
    }

    // ---------- more kinds, phase 2 ----------

    // DoctrineUpgradeSystem.GetIcon reads an Addressables atlas the base loads (Initialise from BaseLocationManager).
    IEnumerator DoctrineIcons()
    {
        var atlas = Traverse.Create(typeof(DoctrineUpgradeSystem)).Field("doctrineIcons");
        for (float t = 0; atlas.GetValue() == null && t < 20f; t += Time.unscaledDeltaTime) yield return null;
        if (atlas.GetValue() == null) { Fail("DoctrineIcons", "the doctrine icon atlas never loaded"); yield break; }
        Try("DoctrineIcons", () =>
        {
            foreach (var d in more.TryGetValue("doctrines", out var l) ? l : new List<Dict>())
            {
                var sprite = DoctrineUpgradeSystem.GetIcon((DoctrineUpgradeSystem.DoctrineType)Enum.Parse(typeof(DoctrineUpgradeSystem.DoctrineType), (string)d["id"]));
                if (sprite) SaveSprite(sprite, "doctrines", (string)d["id"]);
            }
        });
    }

    void TraitIcons()
    {
        foreach (var d in more.TryGetValue("traits", out var l) ? l : new List<Dict>())
            try
            {
                var sprite = FollowerTrait.GetIcon((FollowerTrait.TraitType)Enum.Parse(typeof(FollowerTrait.TraitType), (string)d["id"]));
                if (sprite) SaveSprite(sprite, "traits", (string)d["id"]);
            }
            catch { }  // the atlas has no icon for some (internal) traits
    }

    void CrownIcons()
    {
        var maps = Resources.FindObjectsOfTypeAll<UpgradeTypeMapping>();
        foreach (var t in CrownUpgrades)
        {
            var sprite = maps.Select(m => { try { return m.GetItem(t)?.Sprite; } catch { return null; } }).FirstOrDefault(s => s);
            if (sprite) SaveSprite(sprite, "crownAbilities", t.ToString());
        }
    }

    // The player-upgrades menu's own fleece buttons (FleeceItemBuyable): number, cost (a talisman, or sin when _sinCost
    // is set), icon from its FleeceIconMapping. Names are TarotCards/Fleece{n}/Name (GetFleeceDisplayName uses the wrong term).
    void ReadFleeces()
    {
        var packs = new (string, List<PlayerFleeceManager.FleeceType>)[] { ("sinful", PlayerFleeceManager.SinfulPackFleeces),
            ("heretic", PlayerFleeceManager.HereticPackFleeces), ("pilgrim", PlayerFleeceManager.PilgrimPackFleeces),
            ("woolhaven", PlayerFleeceManager.WoolhavenPackFleeces) };
        var icons = Resources.FindObjectsOfTypeAll<FleeceIconMapping>();
        var list = new List<Dict>();
        // the upgrade menu template's serialized buttons (UIManager loads it with the base assets)
        var menu = Resources.FindObjectsOfTypeAll<Lamb.UI.UIManager>().Select(m => m.PlayerUpgradesMenuTemplate).FirstOrDefault(t => t);
        var buttons = menu ? Traverse.Create(menu).Field("_fleeceItems").GetValue<Lamb.UI.FleeceItemBuyable[]>() : null;
        if (buttons == null || buttons.Length == 0) throw new Exception("no player upgrades menu template with fleece buttons");
        // the menu numbers each button by its ForcedFleeceIndex, or else by its place in the array (UIPlayerUpgradesMenuController)
        var seen = new HashSet<int>();
        for (int i = 0; i < buttons.Length; i++)
        {
            var b = buttons[i];
            if (!b) continue;
            int n = b.ForcedFleeceIndex == -1 ? i : b.ForcedFleeceIndex;
            if (!seen.Add(n)) continue;
            if (English($"TarotCards/Fleece{n}/Name") == null) continue;
            var type = (PlayerFleeceManager.FleeceType)n;
            string id = Enum.IsDefined(typeof(PlayerFleeceManager.FleeceType), n) ? type.ToString() : $"Fleece{n}";
            int sin = Traverse.Create(b).Field("_sinCost").GetValue<int>();
            list.Add(new Dict {
                ["id"] = id, ["nameTerm"] = Term($"TarotCards/Fleece{n}/Name"), ["descTerm"] = Term($"TarotCards/Fleece{n}/Description"),
                ["fleece"] = n, ["cost"] = new List<Dict> { new Dict { ["item"] = sin != -1 ? "PLEASURE_POINT" : "TALISMAN", ["quantity"] = sin != -1 ? sin : 1 } },
                ["pack"] = packs.Where(p => p.Item2.Contains(type)).Select(p => p.Item1).FirstOrDefault(),
            });
            var sprite = icons.Select(m => { try { return m.GetImage(n); } catch { return null; } }).FirstOrDefault(s => s);
            if (sprite) SaveSprite(sprite, "fleeces", id);
        }
        if (list.Count == 0) throw new Exception("no FleeceItemBuyable loaded");
        more["fleeces"] = list;
        Logger.LogInfo($"fleeces: {list.Count}");
    }

    // ---------- portraits: follower forms, tarot cards and outfits have no sprite; the game draws them with Spine ----------

    const int PortraitLayer = 31, PortraitSize = 256;
    readonly Dictionary<string, WorshipperData.SkinAndData> skinSources = new Dictionary<string, WorshipperData.SkinAndData>();

    IEnumerator Portraits()
    {
        var cam = new GameObject("CotlDataDumpCamera").AddComponent<Camera>();
        cam.enabled = false;
        cam.orthographic = true;
        cam.cullingMask = 1 << PortraitLayer;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 100f;
        cam.aspect = 1f;
        int n = 0;
        // Follower forms: the follower skeleton with the form's skin and its first colour set (as the new-form card shows it).
        var follower = WorshipperData.Instance.SkeletonData ? WorshipperData.Instance.SkeletonData.skeletonDataAsset : null;
        Spine.Unity.SkeletonDataAsset[] loaded = null;
        foreach (var kv in skinSources)
        {
            if (!follower) break;
            // A few forms (the Palworld ones) may not be in the follower skeleton: use any loaded skeleton that has the skin.
            var asset = follower;
            string skin = kv.Value.Skin[0].Skin;
            if (follower.GetSkeletonData(true).FindSkin(skin) == null)
            {
                loaded = loaded ?? Resources.FindObjectsOfTypeAll<Spine.Unity.SkeletonDataAsset>();
                asset = loaded.FirstOrDefault(x => { try { return x.GetSkeletonData(true)?.FindSkin(skin) != null; } catch { return false; } });
                Logger.LogInfo($"skin {skin} isn't in the follower skeleton; {(asset ? "found in " + asset.name : "no loaded skeleton has it")}");
                if (!asset) continue;
            }
            yield return Portrait(cam, asset, "skins", kv.Key, s =>
                {
                    s.SetSkin(kv.Value.Skin[0].Skin);
                    if (kv.Value.SlotAndColours.Count > 0)
                        foreach (var sc in kv.Value.SlotAndColours[0].SlotAndColours) s.FindSlot(sc.Slot)?.SetColor(sc.color);
                }, "idle", () => n++);
        }
        // Tarot cards: the card skeleton TarotCardAnimator shows, in its menu pose.
        var tarot = Resources.FindObjectsOfTypeAll<Lamb.UI.TarotCardAnimator>().Select(a => a.Spine ? a.Spine.skeletonDataAsset : null).FirstOrDefault(a => a);
        foreach (var d in more.TryGetValue("tarot", out var cards) ? cards : new List<Dict>())
            if (tarot)
            {
                var card = (TarotCards.Card)Enum.Parse(typeof(TarotCards.Card), (string)d["id"]);
                yield return Portrait(cam, tarot, "tarot", (string)d["id"], s => s.SetSkin(TarotCards.Skin(card)), "menu-static", () => n++);
            }
        // Outfits: their own skeleton with the clothing skin and first colour set (SpineExtensions.ConfigureFollowerOutfit).
        foreach (var d in more.TryGetValue("outfits", out var outfits) ? outfits : new List<Dict>())
        {
            var cd = TailorManager.GetClothingData((FollowerClothingType)Enum.Parse(typeof(FollowerClothingType), (string)d["id"]));
            if (!cd || !cd.SkeletonData || !cd.SkeletonData.skeletonDataAsset) continue;
            yield return Portrait(cam, cd.SkeletonData.skeletonDataAsset, "outfits", (string)d["id"], s =>
            {
                s.SetSkin(FollowerBrain.GetClothingName(cd.ClothingType));
                if (cd.SlotAndColours.Count > 0)
                    foreach (var sc in cd.SlotAndColours[0].SlotAndColours) s.FindSlot(sc.Slot)?.SetColor(sc.color);
            }, "idle", () => n++);
        }
        Destroy(cam.gameObject);
        Logger.LogInfo($"portraits: {n} (follower skeleton {(bool)follower}, tarot skeleton {(bool)tarot})");
    }

    // One skeleton, alone on its layer far from the world: ForceVisible and no worker thread, so it builds its mesh
    // without the gameplay camera seeing it. Drawn on black and on white; the difference gives the alpha.
    IEnumerator Portrait(Camera cam, Spine.Unity.SkeletonDataAsset asset, string kind, string id, Action<Spine.Skeleton> setup, string anim, Action done)
    {
        Spine.Unity.SkeletonAnimation sa = null;
        try
        {
            sa = Spine.Unity.SkeletonAnimation.NewSkeletonAnimationGameObject(asset);
            sa.gameObject.layer = PortraitLayer;
            sa.transform.position = new Vector3(0f, -5000f, 0f);
            sa.ForceVisible = true;
            Traverse.Create(sa).Field("useMultithreading").SetValue(false);
            setup(sa.Skeleton);
            sa.Skeleton.SetSlotsToSetupPose();
            var a = asset.GetSkeletonData(true).FindAnimation(anim);
            if (a != null) sa.AnimationState.SetAnimation(0, a, true);
            sa.Update(0f);
            sa.LateUpdate();
        }
        catch (Exception e) { Logger.LogWarning($"{kind} {id} portrait: {e.Message}"); if (sa) Destroy(sa.gameObject); yield break; }
        yield return null;
        yield return new WaitForEndOfFrame();
        try
        {
            var bounds = sa.GetComponent<MeshRenderer>().bounds;
            if (bounds.size.x <= 0f || bounds.size.y <= 0f) throw new Exception("empty mesh");
            cam.transform.SetPositionAndRotation(bounds.center - Vector3.forward * 20f, Quaternion.identity);
            cam.orthographicSize = Mathf.Max(bounds.extents.x, bounds.extents.y) * 1.06f;
            var black = Shoot(cam, Color.black);
            var white = Shoot(cam, Color.white);
            var px = new Color32[black.Length];
            for (int i = 0; i < px.Length; i++)
            {
                // over black: c*a; over white: c*a + (1-a); so a = 1 - (white - black), c = black / a
                float alpha = 1f - ((white[i].r - black[i].r) + (white[i].g - black[i].g) + (white[i].b - black[i].b)) / (3f * 255f);
                byte A = (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * 255f), 0, 255);
                px[i] = A == 0 ? new Color32(0, 0, 0, 0) : new Color32((byte)Mathf.Min(255, black[i].r / alpha), (byte)Mathf.Min(255, black[i].g / alpha), (byte)Mathf.Min(255, black[i].b / alpha), A);
            }
            var tex = new Texture2D(PortraitSize, PortraitSize, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply();
            string dir = Path.Combine(outDir, "icons", kind);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, id + ".png"), tex.EncodeToPNG());
            Destroy(tex);
            done();
        }
        catch (Exception e) { Logger.LogWarning($"{kind} {id} portrait: {e.Message}"); }
        finally { Destroy(sa.gameObject); }
    }

    static Color32[] Shoot(Camera cam, Color background)
    {
        var rt = RenderTexture.GetTemporary(PortraitSize, PortraitSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        cam.backgroundColor = background;
        cam.targetTexture = rt;
        cam.Render();
        var was = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(PortraitSize, PortraitSize, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, PortraitSize, PortraitSize), 0, 0);
        RenderTexture.active = was;
        cam.targetTexture = null;
        RenderTexture.ReleaseTemporary(rt);
        var px = tex.GetPixels32();
        Destroy(tex);
        return px;
    }

    void ReadTranslations()
    {
        // From the source that holds the English text English() found: another may have the term with an empty column.
        bool HasEnglish(LanguageSourceData source, TermData data)
        {
            int i = source.mLanguages.FindIndex(l => l.Name == "English");
            return i >= 0 && i < data.Languages.Length && !string.IsNullOrEmpty(data.Languages[i]);
        }
        foreach (var pass in new[] { true, false })
        foreach (var source in LocalizationManager.Sources)
            foreach (var term in terms)
            {
                var data = source.GetTermData(term);
                if (data == null || i18n.ContainsKey(term) || (pass && !HasEnglish(source, data))) continue;
                // Columns without a code ("Chara Count", "Comments", ...) aren't languages.
                i18n[term] = source.mLanguages.Select((l, i) => (code: l.Code, text: i < data.Languages.Length ? data.Languages[i] : null))
                    .Where(p => !string.IsNullOrEmpty(p.code) && !string.IsNullOrEmpty(p.text)).ToDictionary(p => p.code, p => p.text);
            }
        Logger.LogInfo($"i18n: {i18n.Count} terms");
    }

    // ---------- phase 2 ----------

    void ItemIcons()
    {
        var maps = Resources.FindObjectsOfTypeAll<InventoryIconMapping>();
        if (maps.Length == 0) throw new Exception("no InventoryIconMapping loaded");
        int n = 0;
        foreach (var item in items ?? new List<Dictionary<string, object>>())
        {
            var t = (InventoryItem.ITEM_TYPE)Enum.Parse(typeof(InventoryItem.ITEM_TYPE), (string)item["id"]);
            var sprite = maps.Select(m => { try { return m.GetImage(t); } catch { return null; } }).FirstOrDefault(sp => sp);
            if (sprite) { SaveSprite(sprite, "items", (string)item["id"]); n++; }
        }
        Logger.LogInfo($"item icons: {n}");
    }

    // The ritual menu's icons (RitualItem uses RitualIconMapping; UpgradeSystem.GetIcon's atlas isn't in the build).
    void RitualIcons()
    {
        var maps = Resources.FindObjectsOfTypeAll<RitualIconMapping>();
        if (maps.Length == 0) throw new Exception("no RitualIconMapping loaded");
        int n = 0;
        foreach (var r in rituals ?? new List<Dictionary<string, object>>())
        {
            var t = (UpgradeSystem.Type)Enum.Parse(typeof(UpgradeSystem.Type), (string)r["id"]);
            var sprite = maps.Select(m => { try { return m.GetImage(t); } catch { return null; } }).FirstOrDefault(sp => sp);
            if (sprite) { SaveSprite(sprite, "rituals", (string)r["id"]); n++; }
        }
        Logger.LogInfo($"ritual icons: {n}");
    }

    void StructureFootprints()
    {
        var list = FindObjectOfType<TypeAndPlacementObjects>();
        if (!list) throw new Exception("no build list (TypeAndPlacementObjects) in the base scene");
        var byId = (structures ?? new List<Dictionary<string, object>>()).ToDictionary(s => (string)s["id"]);
        int n = 0;
        foreach (var entry in list.TypeAndPlacementObject)
        {
            if (!byId.TryGetValue(entry.Type.ToString(), out var s) || s["size"] != null) continue;
            Try($"{entry.Type} footprint", () =>
            {
                var placement = entry.Addr_PlacementObject != null && entry.Addr_PlacementObject.RuntimeKeyIsValid() ? entry.PlacementObject : null;
                var b = placement ? placement.GetComponent<PlacementObject>()?.Bounds : null;
                if (b != null) s["size"] = new Dictionary<string, int> { ["w"] = b.Value.x, ["h"] = b.Value.y };
                if (entry.IconImage) SaveSprite(entry.IconImage, "structures", entry.Type.ToString());
                n++;
            });
        }
        Logger.LogInfo($"structure footprints/icons: {n}");
    }

    // A sprite's own pixels as a PNG (its texture needn't be readable).
    // Icons whose sprite had no texture yet (an atlas not loaded before the base): tried again in phase 2.
    readonly List<(Sprite, string, string)> laterIcons = new List<(Sprite, string, string)>();

    void IconNowOrLater(Sprite sp, string kind, string id)
    {
        if (sp && !SaveSprite(sp, kind, id)) laterIcons.Add((sp, kind, id));
    }

    void LaterIcons()
    {
        int saved = laterIcons.Count(x => SaveSprite(x.Item1, x.Item2, x.Item3));
        if (saved < laterIcons.Count) Logger.LogWarning($"{laterIcons.Count - saved} icons still had no texture in the base");
    }

    // false (nothing written) when the sprite has no texture yet
    bool SaveSprite(Sprite sp, string kind, string id)
    {
        if (!sp || !sp.texture) return false;
        string dir = Path.Combine(outDir, "icons", kind);
        Directory.CreateDirectory(dir);
        var rt = RenderTexture.GetTemporary(sp.texture.width, sp.texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(sp.texture, rt);
        var was = RenderTexture.active;
        RenderTexture.active = rt;
        var rect = sp.textureRect;
        var tex = new Texture2D((int)rect.width, (int)rect.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(rect, 0, 0);
        tex.Apply();
        RenderTexture.active = was;
        RenderTexture.ReleaseTemporary(rt);
        File.WriteAllBytes(Path.Combine(dir, id + ".png"), tex.EncodeToPNG());
        Destroy(tex);
        return true;
    }

    // ---------- helpers ----------

    void Write(string name, object value) =>
        File.WriteAllText(Path.Combine(outDir, name), JsonConvert.SerializeObject(value, Formatting.Indented));

    void Fail(string job, string message)
    {
        Logger.LogError($"{job}: {message}");
        errors.Add(new Dictionary<string, string> { ["job"] = job, ["message"] = message });
    }

    void Try(string job, Action action)
    {
        try { action(); }
        catch (Exception e) { Fail(job, e.ToString()); }
    }
}
