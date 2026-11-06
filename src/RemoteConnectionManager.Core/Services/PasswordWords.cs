namespace RemoteConnectionManager.Core.Services;

/// <summary>
/// Short, common, inoffensive English words for memorable passwords. Distinct, lower case, 3 to 8
/// letters. Four words from this list is roughly 40 bits; the generator shows the strength bar.
/// </summary>
public static class PasswordWords
{
    public static IReadOnlyList<string> All { get; } = Load();

    private static string[] Load() => Source
        .Split((char[])[' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private const string Source = """
        able acid acorn actor adapt adobe agent agree alarm album alert algae alley alpine amber amble
        ample anchor angle ankle apple apron arch arena argue armor arrow aspen atlas atom attic audio
        autumn avid award awake axle bacon badge bagel baker bamboo banjo barn basil basin batch beach
        beacon beam bean bear beaver bench berry bike birch bison blade blank blaze blend bloom blossom
        blue blunt board boat bolt bonus booth border bottle boulder bound bowl brave bread breeze brick
        bridge brief bright brisk broad brook brush bubble bucket budget buffet bugle bundle burrow
        butter button cabin cable cactus cadet cake calm camel camera camp canal candle canoe canvas
        canyon cape carbon cargo carpet carrot cart castle cedar cello chalk charm chart cheese cherry
        chess chief chimney chorus cider cinema circle citrus civic clay clever cliff climb clock cloud
        clover coast cobalt cocoa coffee comet comic coral cosmic cotton cougar cove coyote crane crater
        crayon creek crest cricket crisp crown crystal cube cupcake curve cycle daisy dance dawn decade
        delta denim desert detail dial diary diesel dingo dinner dipper disco dock dolphin domino donut
        dragon drift drum duck dune dusk eagle earth easel echo eclipse elbow elder elm ember emerald
        empire energy engine epic equal error essay ethics event exact exit fabric falcon fancy farm
        feast feather fence ferry fiber field fiesta figure filter final finch fjord flag flame flash
        fleet flint float flock flora flower fluent flute focus fog folio forest forge fossil fox frame
        fresh frost fruit fudge galaxy garden garlic gazebo gecko gentle giant ginger glacier glade
        glass globe glove glow gold gopher grain granite grape graph grass gravel green grid grove guide
        guitar gull habit hammer harbor harp harvest hatch haven hazel heart helmet herb heron hiker
        hill hobby holly honey hornet horse hotel hound humble hybrid icon igloo image index indigo
        inlet input iris island ivory jacket jaguar jasmine jazz jelly jersey jet jewel jigsaw jockey
        jolly journal judge juice jumbo jungle juniper kayak kernel kettle kiwi koala label ladder
        lagoon lake lamp lantern laser latch lava lawn layer leaf ledge legend lemon lens lilac lime
        linen lion lizard llama lobster locket lodge logic lotus lucky lunar lyric magnet mango manor
        maple marble market marsh mask meadow medal melody melon mentor merit mesa metal meteor mile
        mint mirror mocha model module monitor moose mosaic moss motel motor mountain mural museum
        music mustard myth napkin native nebula nectar needle nest noble north notch novel nugget oak
        oasis ocean olive omega onion opal opera orange orbit orchid otter outer owl oxygen oyster
        paddle pagoda palace palm panda panel paper parade parrot pastel patch path peach peanut pearl
        pebble pecan pelican pencil pepper piano picnic pilot pine pioneer pixel pizza planet plaza
        plum polar pony poppy portal potato prairie prism pulse puma pumpkin puzzle quail quartz quest
        quick quiet quilt quota rabbit radar radio radish rain ranch rapid raven reef relay rhythm
        ribbon ridge ripple river robin robot rocket rodeo rose rover ruby rustic saddle safari saga
        salmon salt sample sandal satin saturn savvy scarf scenic scout season seed shadow shark shell
        shore sierra signal silk silver simple sketch skiff slate sleek slope smooth snack snow solar
        sonic spark sphinx spice spiral splash spruce square squid stable stamp star statue steam stone
        storm stream studio summit sunny sunset swan sweet swift symbol syrup table tablet tango taxi
        teapot tempo tender tent thistle thunder tiger timber tinsel toast token tomato topaz torch
        tornado totem tower track trail tribe trophy tropic trout tulip tundra turtle tuxedo twig
        umber unicorn unity upper urban valley vapor velvet venture verse vessel violet violin vista
        vivid voyage wafer waffle wagon walnut walrus wander water wave willow window winter wisdom
        wizard wombat wonder woods yacht yarn yeti yodel yogurt young zebra zenith zero zesty zinc
        zipper zodiac zone
        """;
}
