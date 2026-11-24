using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using IracingOverlay.Core.Race;

namespace IracingOverlay.App.ViewModels;

internal static class DriverIcons
{
    private static readonly BitmapSource BrandSheet = LoadSheet("colored-car-icons-sprite.png");
    private static readonly Dictionary<int, BitmapSource> BrandTiles = [];
    private static readonly Dictionary<string, BitmapSource> Flags = LoadFlags();

    // Cells are 128 px square, numbered left to right across the eight-column colored sheet.
    private static readonly (string Make, int Tile)[] Makes =
    [
        ("Audi", 1), ("BMW", 2), ("Mercedes", 3), ("Ford", 4), ("Chevrolet", 5),
        ("Ferrari", 6), ("Lamborghini", 7), ("McLaren", 8), ("Porsche", 9), ("Dallara", 10),
        ("Aston Martin", 11), ("Volkswagen", 12), ("Honda", 13), ("Toyota", 15),
        ("Pontiac", 16), ("Mazda", 17), ("Lucas Oil", 18), ("Renault", 19), ("Subaru", 20),
        ("Holden", 21), ("Lotus", 22), ("Cadillac", 23), ("Kia", 24), ("Lexus", 25),
        ("Nissan", 26), ("RUF", 29), ("Radical", 31), ("Hyundai", 33), ("Acura", 35),
        ("Buick", 36),
    ];

    public static BitmapSource? Brand(DriverIconInfo car)
    {
        // Some SDK paths abbreviate or omit the manufacturer.
        int tile = car.CarPath.ToLowerInvariant() switch
        {
            "amvalkyriegtp" or "amvantageevogt3" or "amvantagegt4" => 11,
            "c6r" or "c7vettedp" or "c8rvettegte" or "chevyvettez06rgt3" => 5,
            "mx5" or "formulamazda" => 17,
            "jettatdi" or "vwbeetlegrc" => 12,
            "fr500s" => 4,
            "solstice" => 16,
            _ => -1,
        };

        if (tile < 0)
        {
            // Full screen names include the make even for many shared stock-car paths.
            int earliest = int.MaxValue;
            foreach (var (make, index) in Makes)
            {
                int at = car.CarModel.IndexOf(make, StringComparison.OrdinalIgnoreCase);
                if (at < 0 || at >= earliest) continue;
                if (at > 0 && char.IsLetterOrDigit(car.CarModel[at - 1])) continue;
                int end = at + make.Length;
                if (end < car.CarModel.Length && char.IsLetter(car.CarModel[end])) continue;
                earliest = at;
                tile = index;
            }
            if (tile < 0)
                foreach (var (make, index) in Makes)
                    if (car.CarPath.StartsWith(make, StringComparison.OrdinalIgnoreCase))
                    {
                        tile = index;
                        break;
                    }
        }
        if (tile < 0) return null;
        if (!BrandTiles.TryGetValue(tile, out var image))
            BrandTiles[tile] = image = Crop(BrandSheet, tile, 8, 128, 128);
        return image;
    }

    public static BitmapSource Flag(string flair) =>
        Flags.TryGetValue(flair.Trim(), out var flag) ? flag : Flags["iracing"];

    private static readonly Lazy<BitmapSource> BaseSheet = new(() => LoadSheet("base-icons-sprite.png"));
    // Flags in the base icon sheet: 42 black/white (iRacing's furled black = slowdown), 43 meatball, 44 black flag.
    internal static readonly Lazy<BitmapSource> SlowdownFlag = new(() => Crop(BaseSheet.Value, 42, 8, 128, 128));
    internal static readonly Lazy<BitmapSource> RepairFlag = new(() => Crop(BaseSheet.Value, 43, 8, 128, 128));
    internal static readonly Lazy<BitmapSource> BlackFlag = new(() => Crop(BaseSheet.Value, 44, 8, 128, 128));

    // Tyre icons in the 128 px / 8-column base icon sheet: hard, medium, soft, intermediate, wet.
    private static readonly Lazy<Dictionary<TyreCompound, BitmapSource>> Tyres = new(() =>
    {
        var sheet = BaseSheet.Value;
        return new Dictionary<TyreCompound, BitmapSource>
        {
            [TyreCompound.Hard] = Crop(sheet, 30, 8, 128, 128),
            [TyreCompound.Medium] = Crop(sheet, 31, 8, 128, 128),
            [TyreCompound.Soft] = Crop(sheet, 32, 8, 128, 128),
            [TyreCompound.Intermediate] = Crop(sheet, 33, 8, 128, 128),
            [TyreCompound.Wet] = Crop(sheet, 34, 8, 128, 128),
        };
    });

    public static BitmapSource? Tyre(TyreCompound compound) => Tyres.Value.GetValueOrDefault(compound);

    private sealed record FlagEntry(string Code, string Name);
    private sealed record FlagAtlas(int Width, int Height, int Columns, FlagEntry[] Entries);

    private static Dictionary<string, BitmapSource> LoadFlags()
    {
        using var stream = OpenAsset("flag-atlas.json");
        var atlas = JsonSerializer.Deserialize<FlagAtlas>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var sheet = LoadSheet("flag-atlas.png");
        var flags = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < atlas.Entries.Length; i++)
        {
            var entry = atlas.Entries[i];
            var image = Crop(sheet, i, atlas.Columns, atlas.Width, atlas.Height);
            flags[entry.Name] = flags[entry.Code] = image;
            // Some flair labels contain UTF-8 bytes inside the SDK's Latin-1 session text.
            if (!entry.Name.All(char.IsAscii))
                flags[Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(entry.Name))] = image;
        }

        // SDK flair labels also include sporting regions and English names outside CLDR.
        foreach (var (name, code) in new (string, string)[]
        {
            ("England", "gb-eng"), ("Scotland", "gb-sct"), ("Wales", "gb-wls"),
            ("Northern Ireland", "gb-nir"), ("Great Britain", "gb"), ("UK", "gb"),
            ("USA", "us"), ("United States of America", "us"), ("The Netherlands", "nl"),
            ("Czech Republic", "cz"), ("Turkey", "tr"), ("South Korea", "kr"),
            ("Chinese Taipei", "tw"), ("Hong Kong", "hk"), ("Macau", "mo"),
            ("Catalonia", "es-ct"), ("Basque Country", "es-pv"), ("Galicia", "es-ga"),
            ("Canary Islands", "ic"), ("European Union", "eu"),
        })
            flags[name] = flags[code];
        return flags;
    }

    private static Stream OpenAsset(string name) => Application.GetResourceStream(
        new Uri($"pack://application:,,,/IracingOverlay;component/Assets/{name}"))!.Stream;

    private static BitmapSource LoadSheet(string name)
    {
        using var stream = OpenAsset(name);
        var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();
        return image;
    }

    private static BitmapSource Crop(BitmapSource sheet, int index, int columns, int width, int height)
    {
        var image = new CroppedBitmap(sheet, new Int32Rect(index % columns * width, index / columns * height, width, height));
        image.Freeze();
        return image;
    }
}
