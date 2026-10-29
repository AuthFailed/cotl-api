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
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using I2.Loc;
using Lamb.UI.Assets;
using MMTools;
using Newtonsoft.Json;
using src.Managers;
using UnityEngine;
using UnityEngine.SceneManagement;

[BepInPlugin("chrsnv.cotldata.dump", "COTL Data Dump", "0.1.0")]
public class CotlDataDump : BaseUnityPlugin
{
    ConfigEntry<int> slot;
    ConfigEntry<bool> doItems, doStructures, doSkins, doRituals;
    string outDir;
    bool started, finished;
    readonly List<object> errors = new List<object>();
    readonly HashSet<string> terms = new HashSet<string>();
    readonly Dictionary<string, Dictionary<string, string>> i18n = new Dictionary<string, Dictionary<string, string>>();
    List<Dictionary<string, object>> items, structures, skins, rituals;
    readonly Dictionary<string, List<string>> unnamed = new Dictionary<string, List<string>>();
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
        Try("Translations", ReadTranslations);
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
        if (doStructures.Value) Try("StructureFootprints", StructureFootprints);
        finished = true;
        Write("items.json", items ?? new List<Dictionary<string, object>>());
        Write("structures.json", structures ?? new List<Dictionary<string, object>>());
        Write("skins.json", skins ?? new List<Dictionary<string, object>>());
        Write("rituals.json", rituals ?? new List<Dictionary<string, object>>());
        Write("i18n.json", i18n);
        Write("meta.json", new Dictionary<string, object> {
            ["gameVersion"] = Application.version, ["pluginVersion"] = Info.Metadata.Version.ToString(),
            ["capturedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["errors"] = errors,
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

    // Enum.GetNames: GetValues gives one name for aliased values. Types without an English name are internal.
    static IEnumerable<T> Named<T>(string prefix) =>
        Enum.GetNames(typeof(T)).Where(n => English($"{prefix}/{n}") != null).Select(n => (T)Enum.Parse(typeof(T), n));

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
        structures = Named<StructureBrain.TYPES>("Structures").Select(t =>
        {
            StructuresData info = null;
            try { info = StructuresData.GetInfoByType(t, 0); } catch { }
            return new Dictionary<string, object> {
                ["id"] = t.ToString(), ["nameTerm"] = Term($"Structures/{t}"), ["descTerm"] = Term($"Structures/{t}/Description"),
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
        foreach (var c in WorshipperData.Instance.Characters.Where(c => !c.Invariant && c.Skin.Count > 0))
        {
            string id = new string(c.Skin[0].Skin.Select(ch => char.IsLetterOrDigit(ch) && ch < 128 || ch == '_' || ch == '-' ? ch : '_').ToArray());
            // Some forms are listed twice (e.g. for two drop locations); the first entry is the one the game finds.
            if (i18n.ContainsKey($"Skins/{id}")) { Logger.LogWarning($"skin {id} listed again as {c.Title}, skipped"); continue; }
            // The game has no translated skin names, only this title.
            i18n[$"Skins/{id}"] = new Dictionary<string, string> { [en] = c.Title };
            skins.Add(new Dictionary<string, object> {
                ["id"] = id, ["nameTerm"] = $"Skins/{id}", ["unlock"] = c.DropLocation.ToString(), ["hidden"] = c.Hidden,
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
            try { var icon = UpgradeSystem.GetIcon(r); if (icon) SaveSprite(icon, "rituals", r.ToString()); } catch (Exception e) { Logger.LogWarning($"{r} icon: {e.Message}"); }
        }
        Logger.LogInfo($"rituals: {rituals.Count}");
    }

    void ReadTranslations()
    {
        foreach (var source in LocalizationManager.Sources)
            foreach (var term in terms)
            {
                var data = source.GetTermData(term);
                if (data == null || i18n.ContainsKey(term)) continue;
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
    void SaveSprite(Sprite sp, string kind, string id)
    {
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
