using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;

namespace CreatorPantry.Domain.Modules.Vocabulary.Seeding;

/// <summary>
/// The shared vocabularies: food categories, dietary profiles, allergens, cuisines, courses, techniques, and
/// equipment types, with the surface forms that resolve to them. Tier A — seeded in every environment.
/// </summary>
/// <remarks>
/// <para>
/// Names and definitions only. Nothing here asserts a fact about an ingredient, a nutrition figure, or a
/// safety outcome — those live in the trait and density tables, each carrying its own cited source. This file
/// therefore needs no <see cref="ReferenceSource"/> and is licensing-clean for production.
/// </para>
/// <para>
/// Aliases are thin on purpose. <see cref="VocabularyPolicy.NormalizeAlias"/> already folds case and
/// punctuation, so <c>Tex Mex</c> is not worth storing against the display name <c>Tex-Mex</c>; and most
/// apparent cuisine synonyms (Sichuan for Chinese, Cajun for Southern) are narrower terms rather than aliases,
/// which is a distinction an alias table cannot express. Only genuine alternative wordings are seeded.
/// </para>
/// </remarks>
internal static class CatalogueSeedData
{
    private const string FoodCategoryTable = "FoodCategories";
    private const string DietaryProfileTable = "DietaryProfiles";
    private const string AllergenTable = "Allergens";
    private const string CuisineTable = "Cuisines";
    private const string CuisineAliasTable = "CuisineAliases";
    private const string CourseTable = "Courses";
    private const string CourseAliasTable = "CourseAliases";
    private const string CookingTechniqueTable = "CookingTechniques";
    private const string CookingTechniqueAliasTable = "CookingTechniqueAliases";
    private const string EquipmentTypeTable = "EquipmentTypes";
    private const string EquipmentTypeAliasTable = "EquipmentTypeAliases";

    public static Guid FoodCategoryId(string code) => SeedId.For(FoodCategoryTable, code);

    public static Guid DietaryProfileId(string code) => SeedId.For(DietaryProfileTable, code);

    public static Guid AllergenId(string code) => SeedId.For(AllergenTable, code);

    public static IReadOnlyList<FoodCategory> FoodCategories() =>
    [
        .. new (string Code, string DisplayName)[]
        {
            ("baking", "Baking"),
            ("dairy-and-eggs", "Dairy & Eggs"),
            ("produce", "Produce"),
            ("herbs-and-spices", "Herbs & Spices"),
            ("meat-and-poultry", "Meat & Poultry"),
            ("seafood", "Seafood"),
            ("grains-and-pasta", "Grains & Pasta"),
            ("legumes-and-pulses", "Legumes & Pulses"),
            ("nuts-and-seeds", "Nuts & Seeds"),
            ("oils-and-fats", "Oils & Fats"),
            ("condiments-and-sauces", "Condiments & Sauces"),
            ("sweeteners", "Sweeteners"),
            ("pantry-staples", "Pantry Staples"),
        }.Select(category => new FoodCategory
        {
            Id = FoodCategoryId(category.Code),
            Code = category.Code,
            DisplayName = category.DisplayName,
            IsActive = true,
        }),
    ];

    /// <summary>
    /// Dietary patterns a creator describes a recipe against.
    /// </summary>
    /// <remarks>
    /// Three of these — dairy-free, gluten-free, egg-free — sit next to the allergen model, and their
    /// descriptions do the work of keeping them apart. A profile is an editorial tag describing how a recipe
    /// was written. It is not a determination about cross-contact, a production line, or anyone's tolerance,
    /// and the description says so in each case rather than relying on a reader to infer it.
    /// </remarks>
    public static IReadOnlyList<DietaryProfile> DietaryProfiles() =>
    [
        .. new (string Code, string DisplayName, string Description)[]
        {
            ("vegan", "Vegan",
                "Excludes all animal-derived ingredients, including meat, poultry, seafood, dairy, eggs, and honey. "
                    + "Whether a given ingredient qualifies is recorded separately as a cited trait, never inferred."),
            ("vegetarian", "Vegetarian",
                "Excludes meat, poultry, and seafood. Dairy and eggs are included unless a recipe says otherwise."),
            ("pescatarian", "Pescatarian",
                "Excludes meat and poultry; includes seafood. Dairy and eggs are included unless a recipe says otherwise."),
            ("dairy-free", "Dairy-Free",
                "Describes a recipe written without milk-derived ingredients. A descriptive editorial tag only: it is "
                    + "not an allergen determination, and it says nothing about shared equipment or trace exposure. What "
                    + "a source states about milk in an ingredient is recorded in the allergen model instead."),
            ("gluten-free", "Gluten-Free",
                "Describes a recipe written without wheat, barley, rye, or their derivatives. A descriptive editorial "
                    + "tag only: it is not a coeliac-safety determination, and it says nothing about cross-contact "
                    + "during growing, milling, or production."),
            ("egg-free", "Egg-Free",
                "Describes a recipe written without eggs or egg-derived ingredients. A descriptive editorial tag only, "
                    + "not an allergen determination."),
        }.Select(profile => new DietaryProfile
        {
            Id = DietaryProfileId(profile.Code),
            Code = profile.Code,
            DisplayName = profile.DisplayName,
            Description = profile.Description,
            IsActive = true,
        }),
    ];

    /// <summary>
    /// The allergen vocabulary.
    /// </summary>
    /// <remarks>
    /// These codes resemble the groups several jurisdictions require to be declared, and that resemblance is
    /// deliberately not stated anywhere in the data: <see cref="Allergen"/> carries no regulatory
    /// classification, because declaration lists differ by country and by year. Each description instead fixes
    /// what the entry <em>covers</em>, since that is what decides the meaning of every trait recorded against
    /// it, and names the boundary cases rather than silently resolving them.
    /// </remarks>
    public static IReadOnlyList<Allergen> Allergens() =>
    [
        .. new (string Code, string DisplayName, string Description)[]
        {
            ("milk", "Milk",
                "Milk and milk-derived ingredients, including butter, cream, cheese, yoghurt, whey, and casein. Sources "
                    + "differ on whether clarified butter and highly refined lactose are included; each trait records "
                    + "what its own source stated."),
            ("egg", "Egg",
                "Eggs from birds and egg-derived ingredients such as albumen, lysozyme, meringue, and mayonnaise."),
            ("fish", "Fish",
                "Finned fish and fish-derived ingredients such as anchovy, fish sauce, and Worcestershire sauce brewed "
                    + "with anchovy."),
            ("crustacean-shellfish", "Crustacean Shellfish",
                "Crustaceans such as shrimp, prawn, crab, lobster, and crayfish. Molluscs — clam, mussel, oyster, "
                    + "scallop, squid — are grouped here by some sources and separately by others; each trait records "
                    + "what its own source stated."),
            ("tree-nuts", "Tree Nuts",
                "Nuts from trees, including almond, walnut, pecan, cashew, pistachio, hazelnut, macadamia, and Brazil "
                    + "nut. Whether coconut is included varies by source and by jurisdiction; CreatorPantry does not "
                    + "decide it, and each trait records what its own source stated."),
            ("peanut", "Peanut",
                "Peanuts and peanut-derived ingredients. A legume rather than a tree nut, and recorded separately for "
                    + "that reason."),
            ("wheat", "Wheat",
                "Wheat and wheat-derived ingredients, including spelt, durum, semolina, and farro. Barley and rye are "
                    + "not covered by this entry."),
            ("soy", "Soy",
                "Soybeans and soy-derived ingredients such as tofu, tempeh, miso, edamame, and soy lecithin."),
            ("sesame", "Sesame",
                "Sesame seeds and sesame-derived ingredients such as tahini and sesame oil."),
        }.Select(allergen => new Allergen
        {
            Id = AllergenId(allergen.Code),
            Code = allergen.Code,
            DisplayName = allergen.DisplayName,
            Description = allergen.Description,
            IsActive = true,
        }),
    ];

    public static IReadOnlyList<Cuisine> Cuisines() =>
        Vocabulary<Cuisine>(CuisineTable,
            ("american", "American"),
            ("southern-us", "Southern US"),
            ("tex-mex", "Tex-Mex"),
            ("mexican", "Mexican"),
            ("caribbean", "Caribbean"),
            ("italian", "Italian"),
            ("french", "French"),
            ("spanish", "Spanish"),
            ("greek", "Greek"),
            ("mediterranean", "Mediterranean"),
            ("middle-eastern", "Middle Eastern"),
            ("indian", "Indian"),
            ("thai", "Thai"),
            ("vietnamese", "Vietnamese"),
            ("chinese", "Chinese"),
            ("japanese", "Japanese"),
            ("korean", "Korean"));

    public static IReadOnlyList<CuisineAlias> CuisineAliases() =>
        VocabularyAliases<CuisineAlias>(CuisineAliasTable, CuisineTable,
            ("american", ["USA", "United States"]),
            ("southern-us", ["Southern"]),
            ("tex-mex", ["TexMex"]),
            ("caribbean", ["West Indian"]),
            ("italian", ["Italiana"]),
            ("french", ["Française"]));

    /// <summary>
    /// The meal-role vocabulary. One axis covering what the requirements variously call course, meal type, and
    /// dish type — see <see cref="Course"/> for why they are not three tables.
    /// </summary>
    /// <remarks>
    /// "Dinner" is an alias of <c>main-course</c> rather than an entry of its own, while breakfast, brunch, and
    /// lunch are entries. The asymmetry is real and intended: those three name a dish written for that meal,
    /// whereas "dinner recipe" in practice means the main course, and seeding both would put two entries on one
    /// axis for a recipe that can only carry one.
    /// </remarks>
    public static IReadOnlyList<Course> Courses() =>
        Vocabulary<Course>(CourseTable,
            ("breakfast", "Breakfast"),
            ("brunch", "Brunch"),
            ("lunch", "Lunch"),
            ("appetizer", "Appetizer"),
            ("main-course", "Main Course"),
            ("side", "Side"),
            ("salad", "Salad"),
            ("soup", "Soup"),
            ("bread", "Bread"),
            ("baked-good", "Baked Good"),
            ("dessert", "Dessert"),
            ("snack", "Snack"),
            ("drink", "Drink"),
            ("sauce-condiment", "Sauce & Condiment"));

    public static IReadOnlyList<CourseAlias> CourseAliases() =>
        VocabularyAliases<CourseAlias>(CourseAliasTable, CourseTable,
            // "Entrée" and "Entree" normalize identically, so only one of the two is stored.
            ("main-course", ["Entrée", "Main", "Main Dish", "Dinner"]),
            ("side", ["Side Dish"]),
            ("appetizer", ["Starter", "Appetiser", "Hors d'oeuvre"]),
            ("dessert", ["Pudding", "Sweet"]),
            ("drink", ["Beverage"]));

    /// <summary>
    /// The cooking-method vocabulary, with the safety-caution flag recipes.md requires a structured hook for.
    /// </summary>
    /// <remarks>
    /// Eight entries carry <see cref="CookingTechnique.RequiresSafetyCaution"/>. The other twenty-two do not,
    /// and per that property's own documentation this must be read in one direction only: <c>false</c> means no
    /// caution has been attached, never that the technique is safe.
    /// </remarks>
    public static IReadOnlyList<CookingTechnique> CookingTechniques() =>
    [
        .. new (string Code, string DisplayName, bool RequiresSafetyCaution)[]
        {
            ("bake", "Bake", false),
            ("roast", "Roast", false),
            ("broil", "Broil", false),
            ("grill", "Grill", false),
            ("sear", "Sear", false),
            ("pan-fry", "Pan-Fry", false),
            ("deep-fry", "Deep-Fry", false),
            ("stir-fry", "Stir-Fry", false),
            ("saute", "Sauté", false),
            ("boil", "Boil", false),
            ("simmer", "Simmer", false),
            ("poach", "Poach", false),
            ("steam", "Steam", false),
            ("braise", "Braise", false),
            ("stew", "Stew", false),
            ("slow-cook", "Slow-Cook", false),
            ("pressure-cook", "Pressure-Cook", false),
            ("microwave", "Microwave", false),
            ("no-cook", "No-Cook", false),
            ("blend", "Blend", false),
            ("whip", "Whip", false),
            ("knead", "Knead", false),

            // Getting these wrong is a food-safety outcome rather than a disappointing dinner.
            ("water-bath-canning", "Water-Bath Canning", true),
            ("pressure-canning", "Pressure Canning", true),
            ("ferment", "Ferment", true),
            ("cure", "Cure", true),
            ("smoke", "Smoke", true),
            ("sous-vide", "Sous-Vide", true),
            ("dehydrate", "Dehydrate", true),
            ("infuse-oil", "Infuse Oil", true),
        }.Select(technique => new CookingTechnique
        {
            Id = SeedId.For(CookingTechniqueTable, technique.Code),
            Code = technique.Code,
            DisplayName = technique.DisplayName,
            RequiresSafetyCaution = technique.RequiresSafetyCaution,
            IsActive = true,
        }),
    ];

    public static IReadOnlyList<CookingTechniqueAlias> CookingTechniqueAliases() =>
        VocabularyAliases<CookingTechniqueAlias>(CookingTechniqueAliasTable, CookingTechniqueTable,
            ("pan-fry", ["Fry", "Shallow Fry"]),
            ("deep-fry", ["Deep Frying"]),
            ("slow-cook", ["Slow Cooking"]),
            ("sous-vide", ["Immersion Cooking"]),
            ("no-cook", ["Raw", "Uncooked"]),
            ("water-bath-canning", ["Boiling Water Canning"]));

    public static IReadOnlyList<EquipmentType> EquipmentTypes() =>
        Vocabulary<EquipmentType>(EquipmentTypeTable,
            ("oven", "Oven"),
            ("stovetop", "Stovetop"),
            ("outdoor-grill", "Outdoor Grill"),
            ("air-fryer", "Air Fryer"),
            ("sheet-pan", "Sheet Pan"),
            ("baking-dish", "Baking Dish"),
            ("cake-pan", "Cake Pan"),
            ("loaf-pan", "Loaf Pan"),
            ("muffin-tin", "Muffin Tin"),
            ("pie-dish", "Pie Dish"),
            ("skillet", "Skillet"),
            ("cast-iron-skillet", "Cast-Iron Skillet"),
            ("saucepan", "Saucepan"),
            ("stockpot", "Stockpot"),
            ("dutch-oven", "Dutch Oven"),
            ("wok", "Wok"),
            ("stand-mixer", "Stand Mixer"),
            ("hand-mixer", "Hand Mixer"),
            ("food-processor", "Food Processor"),
            ("blender", "Blender"),
            ("immersion-blender", "Immersion Blender"),
            ("slow-cooker", "Slow Cooker"),
            ("pressure-cooker", "Pressure Cooker"),
            ("mandoline", "Mandoline"),
            ("kitchen-scale", "Kitchen Scale"),
            ("thermometer", "Thermometer"));

    /// <summary>
    /// Equipment surface forms.
    /// </summary>
    /// <remarks>
    /// No brand names, including the ones that have drifted toward generic use in recipe writing ("crock pot",
    /// "instant pot"). <see cref="EquipmentType"/> excludes brand from the catalogue, and an alias is still a
    /// catalogue row. Recognising them belongs to the import parser, with the trademark outside the shared
    /// vocabulary.
    /// </remarks>
    public static IReadOnlyList<EquipmentTypeAlias> EquipmentTypeAliases() =>
        VocabularyAliases<EquipmentTypeAlias>(EquipmentTypeAliasTable, EquipmentTypeTable,
            ("stovetop", ["Hob", "Cooktop"]),
            ("sheet-pan", ["Baking Sheet", "Cookie Sheet", "Half Sheet Pan"]),
            ("baking-dish", ["Casserole Dish"]),
            ("skillet", ["Frying Pan", "Frypan"]),
            ("kitchen-scale", ["Food Scale"]),
            ("thermometer", ["Instant-Read Thermometer"]));

    private static IReadOnlyList<TVocabulary> Vocabulary<TVocabulary>(
        string table,
        params (string Code, string DisplayName)[] entries)
        where TVocabulary : ControlledVocabulary, new() =>
    [
        .. entries.Select(entry => new TVocabulary
        {
            Id = SeedId.For(table, entry.Code),
            Code = entry.Code,
            DisplayName = entry.DisplayName,
            IsActive = true,
        }),
    ];

    private static IReadOnlyList<TAlias> VocabularyAliases<TAlias>(
        string aliasTable,
        string vocabularyTable,
        params (string Code, string[] Aliases)[] definitions)
        where TAlias : VocabularyAlias, new() =>
    [
        .. definitions.SelectMany(definition => definition.Aliases.Select(alias => new TAlias
        {
            Id = SeedId.For(aliasTable, VocabularyPolicy.NormalizeAlias(alias)),
            VocabularyId = SeedId.For(vocabularyTable, definition.Code),
            Alias = alias,
            NormalizedAlias = VocabularyPolicy.NormalizeAlias(alias),
        })),
    ];
}
