using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mapster.Common.MemoryMappedTypes;

/// <summary>
///     Action to be called when iterating over <see cref="MapFeature" /> in a given bounding box via a call to
///     <see cref="DataFile.ForeachFeature" />
/// </summary>
/// <param name="feature">The current <see cref="MapFeature" />.</param>
/// <param name="label">The label of the feature, <see cref="string.Empty" /> if not available.</param>
/// <param name="coordinates">The coordinates of the <see cref="MapFeature" />.</param>
/// <returns></returns>
public delegate bool MapFeatureDelegate(MapFeatureData featureData);

/// <summary>
///     Aggregation of all the data needed to render a map feature
/// </summary>



public readonly ref struct MapFeatureData
{
    public long Id { get; init; }

    public GeometryType Type { get; init; }
    public ReadOnlySpan<char> Label { get; init; }
    public ReadOnlySpan<Coordinate> Coordinates { get; init; }
    public Props Properties { get; init; }
}

/// <summary>
///     Represents a file with map data organized in the following format:<br />
///     <see cref="FileHeader" /><br />
///     Array of <see cref="TileHeaderEntry" /> with <see cref="FileHeader.TileCount" /> records<br />
///     Array of tiles, each tile organized:<br />
///     <see cref="TileBlockHeader" /><br />
///     Array of <see cref="MapFeature" /> with <see cref="TileBlockHeader.FeaturesCount" /> at offset
///     <see cref="TileHeaderEntry.OffsetInBytes" /> + size of <see cref="TileBlockHeader" /> in bytes.<br />
///     Array of <see cref="Coordinate" /> with <see cref="TileBlockHeader.CoordinatesCount" /> at offset
///     <see cref="TileBlockHeader.CharactersOffsetInBytes" />.<br />
///     Array of <see cref="StringEntry" /> with <see cref="TileBlockHeader.StringCount" /> at offset
///     <see cref="TileBlockHeader.StringsOffsetInBytes" />.<br />
///     Array of <see cref="char" /> with <see cref="TileBlockHeader.CharactersCount" /> at offset
///     <see cref="TileBlockHeader.CharactersOffsetInBytes" />.<br />
/// </summary>
public unsafe class DataFile : IDisposable
{
    private readonly FileHeader* _fileHeader;
    private readonly MemoryMappedViewAccessor _mma;
    private readonly MemoryMappedFile _mmf;

    private readonly byte* _ptr;
    private readonly int CoordinateSizeInBytes = Marshal.SizeOf<Coordinate>();
    private readonly int FileHeaderSizeInBytes = Marshal.SizeOf<FileHeader>();
    private readonly int MapFeatureSizeInBytes = Marshal.SizeOf<MapFeature>();
    private readonly int StringEntrySizeInBytes = Marshal.SizeOf<StringEntry>();
    private readonly int TileBlockHeaderSizeInBytes = Marshal.SizeOf<TileBlockHeader>();
    private readonly int TileHeaderEntrySizeInBytes = Marshal.SizeOf<TileHeaderEntry>();

    private bool _disposedValue;

    public DataFile(string path)
    {
        _mmf = MemoryMappedFile.CreateFromFile(path);
        _mma = _mmf.CreateViewAccessor();
        _mma.SafeMemoryMappedViewHandle.AcquirePointer(ref _ptr);
        _fileHeader = (FileHeader*)_ptr;
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _mma?.SafeMemoryMappedViewHandle.ReleasePointer();
                _mma?.Dispose();
                _mmf?.Dispose();
            }

            _disposedValue = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private TileHeaderEntry* GetNthTileHeader(int i)
    {
        return (TileHeaderEntry*)(_ptr + i * TileHeaderEntrySizeInBytes + FileHeaderSizeInBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private (TileBlockHeader? Tile, ulong TileOffset) GetTile(int tileId)
    {
        ulong tileOffset = 0;
        for (var i = 0; i < _fileHeader->TileCount; ++i)
        {
            var tileHeaderEntry = GetNthTileHeader(i);
            if (tileHeaderEntry->ID == tileId)
            {
                tileOffset = tileHeaderEntry->OffsetInBytes;
                return (*(TileBlockHeader*)(_ptr + tileOffset), tileOffset);
            }
        }

        return (null, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private MapFeature* GetFeature(int i, ulong offset)
    {
        return (MapFeature*)(_ptr + offset + TileBlockHeaderSizeInBytes + i * MapFeatureSizeInBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private ReadOnlySpan<Coordinate> GetCoordinates(ulong coordinateOffset, int ithCoordinate, int coordinateCount)
    {
        return new ReadOnlySpan<Coordinate>(_ptr + coordinateOffset + ithCoordinate * CoordinateSizeInBytes, coordinateCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void GetString(ulong stringsOffset, ulong charsOffset, int i, out ReadOnlySpan<char> value)
    {
        var stringEntry = (StringEntry*)(_ptr + stringsOffset + i * StringEntrySizeInBytes);
        value = new ReadOnlySpan<char>(_ptr + charsOffset + stringEntry->Offset * 2, stringEntry->Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void GetProperty(ulong stringsOffset, ulong charsOffset, int i, out ReadOnlySpan<char> key, out ReadOnlySpan<char> value)
    {
        if (i % 2 != 0)
        {
            throw new ArgumentException("Properties are key-value pairs and start at even indices in the string list (i.e. i % 2 == 0)");
        }

        GetString(stringsOffset, charsOffset, i, out key);
        GetString(stringsOffset, charsOffset, i + 1, out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void ForeachFeature(BoundingBox b, MapFeatureDelegate? action)
    {
        if (action == null)
        {
            return;
        }

        var tiles = TiligSystem.GetTilesForBoundingBox(b.MinLat, b.MinLon, b.MaxLat, b.MaxLon);
        for (var i = 0; i < tiles.Length; ++i)
        {
            var header = GetTile(tiles[i]);
            if (header.Tile == null)
            {
                continue;
            }
            for (var j = 0; j < header.Tile.Value.FeaturesCount; ++j)
            {
                var feature = GetFeature(j, header.TileOffset);
                var coordinates = GetCoordinates(header.Tile.Value.CoordinatesOffsetInBytes, feature->CoordinateOffset, feature->CoordinateCount);
                var isFeatureInBBox = false;

                for (var k = 0; k < coordinates.Length; ++k)
                {
                    if (b.Contains(coordinates[k]))
                    {
                        isFeatureInBBox = true;
                        break;
                    }
                }

                var label = ReadOnlySpan<char>.Empty;
                if (feature->LabelOffset >= 0)
                {
                    GetString(header.Tile.Value.StringsOffsetInBytes, header.Tile.Value.CharactersOffsetInBytes, feature->LabelOffset, out label);
                }

                if (isFeatureInBBox)
                {
                    var properties = new Dictionary<string, string>(feature->PropertyCount);
                    for (var p = 0; p < feature->PropertyCount; ++p)
                    {
                        GetProperty(header.Tile.Value.StringsOffsetInBytes, header.Tile.Value.CharactersOffsetInBytes, p * 2 + feature->PropertiesOffset, out var key, out var value);
                        properties.Add(key.ToString(), value.ToString());
                    }

                    if (!action(new MapFeatureData
                    {
                        Id = feature->Id,
                        Label = label,
                        Coordinates = coordinates,
                        Type = feature->GeometryType,
                        Properties = new Props(properties)
                    }))
                    {
                        break;
                    }
                }
            }
        }
    }
}

public class Props
{

    public enum Highway
    {
        other,
        unset,
        motorway,
        trunk,
        primary,
        secondary,
        tertiary,
        unclassified,
        residential,
        road,
    };

    private static Dictionary<string, Highway> HighwayDict = new Dictionary<string, Highway>(){
      { "motorway", Highway.motorway },
      { "trunk", Highway.trunk },
      { "primary", Highway.primary },
      { "secondary", Highway.secondary },
      { "tertiary", Highway.tertiary },
      { "unclassified", Highway.unclassified },
      { "residential", Highway.residential },
      { "road", Highway.road },
  };

    public enum Water
    {
        Other,
        Unset,
    };

    private static Dictionary<string, Water> WaterMapping = new Dictionary<string, Water>() { };

    public enum Boundary
    {
        Other,
        Unset,
        Administrative,
        Forest,
    };

    private static Dictionary<string, Boundary> BoundaryMapping = new Dictionary<string, Boundary>(){
    { "administrative", Boundary.Administrative },
    { "forest", Boundary.Forest },
  };

    public enum AdminLevel
    {
        OTHER,
        UNSET,
        LEVEL_2,
    };

    private static Dictionary<string, AdminLevel> AdminLevelMapping = new Dictionary<string, AdminLevel>() {
    { "2", AdminLevel.LEVEL_2 },
  };

    public enum Place
    {
        OTHER,
        UNSET,
        CITY,
        TOWN,
        LOCALITY,
        HAMLET,
    };

    private static Dictionary<string, Place> PlaceMapping = new Dictionary<string, Place>() {
    { "city", Place.CITY },
    { "town", Place.TOWN },
    { "locality", Place.LOCALITY },
    { "hamlet", Place.HAMLET }
  };

    public enum Railway
    {
        OTHER,
        UNSET,
    };

    private static Dictionary<string, Railway> RailwayMapping = new Dictionary<string, Railway>() { };

    public enum Natural
    {
        OTHER,
        UNSET,
        FELL,
        GRASSLAND,
        HEATH,
        MOOR,
        SCRUB,
        WETLAND,
        WOOD,
        TREE_ROW,
        BARE_ROCK,
        ROCK,
        SCREE,
        BEACH,
        SAND,
        WATER
    };

    private static Dictionary<string, Natural> NaturalMapping = new Dictionary<string, Natural>() {
    { "fell", Natural.FELL },
    { "grassland", Natural.GRASSLAND },
    { "heath", Natural.HEATH },
    { "moor", Natural.MOOR },
    { "scrub", Natural.SCRUB },
    { "wetland", Natural.WETLAND },
    { "wood", Natural.WOOD },
    { "tree_row", Natural.TREE_ROW },
    { "bare_rock", Natural.BARE_ROCK },
    { "rock", Natural.ROCK },
    { "scree", Natural.SCREE },
    { "beach", Natural.BEACH },
    { "sand", Natural.SAND },
    { "water", Natural.WATER },
  };

    public enum Landuse
    {
        OTHER,
        UNSET,
        FOREST,
        ORCHARD,
        RESIDENTIAL,
        CEMETERY,
        INDUSTRIAL,
        COMMERCIAL,
        SQUARE,
        CONSTRUCTION,
        MILITARY,
        QUARRY,
        BROWNFIELD,
        FARM,
        MEADOW,
        GRASS,
        GREENFIELD,
        RECREATION_GROUND,
        WINTER_SPORTS,
        ALLOTMENTS,
        RESERVOIR,
        BASIN,
    };

    private static Dictionary<string, Landuse> LanduseMapping = new Dictionary<string, Landuse>() {
    { "forest", Landuse.FOREST },
    { "orchard", Landuse.ORCHARD },
    { "residential", Landuse.RESIDENTIAL },
    { "cemetery", Landuse.CEMETERY },
    { "industrial", Landuse.INDUSTRIAL },
    { "commercial", Landuse.COMMERCIAL },
    { "square", Landuse.SQUARE },
    { "construction", Landuse.CONSTRUCTION },
    { "military", Landuse.MILITARY },
    { "quarry", Landuse.QUARRY },
    { "brownfield", Landuse.BROWNFIELD },
    { "farm", Landuse.FARM },
    { "meadow", Landuse.MEADOW },
    { "grass", Landuse.GRASS },
    { "greenfield", Landuse.GREENFIELD },
    { "recreation_ground", Landuse.RECREATION_GROUND },
    { "winter_sports", Landuse.WINTER_SPORTS },
    { "allotments", Landuse.ALLOTMENTS },
    { "reservoir", Landuse.RESERVOIR },
    { "basin", Landuse.BASIN },
  };

    public enum Building
    {
        OTHER,
        UNSET,
    };

    private static Dictionary<string, Building> BuildingMapping = new Dictionary<string, Building>() { };

    public enum Leisure
    {
        OTHER,
        UNSET,
    };

    private static Dictionary<string, Leisure> LeisureMapping = new Dictionary<string, Leisure>() { };

    public enum Amenity
    {
        OTHER,
        UNSET,
    };

    private static Dictionary<string, Amenity> AmenityMapping = new Dictionary<string, Amenity>() { };

    public Props(Dictionary<string, string> properties)
    {
        foreach (KeyValuePair<string, string> entry in properties)
        {
            if (entry.Key.StartsWith("highway"))
            {
                string key = HighwayDict.Keys.Where(k => entry.Value.StartsWith(k)).FirstOrDefault("");
                HighwayDict.TryGetValue(key, out highway);
            }
            else if (entry.Key.StartsWith("water"))
            {
                string key = WaterMapping.Keys.Where(k => entry.Value.StartsWith(k)).FirstOrDefault("");
                WaterMapping.TryGetValue(key, out water);
            }
            else if (entry.Key.StartsWith("boundary"))
            {
                string key = BoundaryMapping.Keys.Where(k => entry.Value.StartsWith(k)).FirstOrDefault("");
                BoundaryMapping.TryGetValue(key, out boundary);
            }
            else if (entry.Key.StartsWith("railway"))
            {
                string key = RailwayMapping.Keys.Where(k => entry.Value.StartsWith(k)).FirstOrDefault("");
                RailwayMapping.TryGetValue(key, out railway);
            }
            else if (entry.Key.StartsWith("natural"))
            {
                string key = NaturalMapping.Keys.Where(k => entry.Value.StartsWith(k)).FirstOrDefault("");
                NaturalMapping.TryGetValue(key, out natural);
            }
            else if (entry.Key.StartsWith("landuse"))
            {
                string key = LanduseMapping.Keys.Where(k => entry.Value.StartsWith(k)).FirstOrDefault("");
                LanduseMapping.TryGetValue(key, out landuse);
            }
            else if (entry.Key.StartsWith("building"))
            {
                string key = BuildingMapping.Keys.Where(k => entry.Value.StartsWith(k)).FirstOrDefault("");
                BuildingMapping.TryGetValue(key, out building);
            }
            else if (entry.Key.StartsWith("leisure"))
            {
                string key = LeisureMapping.Keys.Where(k => entry.Value.StartsWith(k)).FirstOrDefault("");
                LeisureMapping.TryGetValue(key, out leisure);
            }
            else if (entry.Key.StartsWith("amenity"))
            {
                string key = AmenityMapping.Keys.Where(k => entry.Value.StartsWith(k)).FirstOrDefault("");
                AmenityMapping.TryGetValue(key, out amenity);
            }
            else if (entry.Key == "admin_level")
            {
                string key = AdminLevelMapping.Keys.Where(k => entry.Value.StartsWith(k)).FirstOrDefault("");
                AdminLevelMapping.TryGetValue(key, out adminLevel);
            }
            else if (entry.Key.StartsWith("place"))
            {
                string key = PlaceMapping.Keys.Where(k => entry.Value.StartsWith(k)).FirstOrDefault("");
                PlaceMapping.TryGetValue(key, out place);
            }
            else if (entry.Key.StartsWith("name"))
            {
                name = entry.Value;
            }
        }
    }


    public Highway highway = Highway.unset;
    public Water water = Water.Unset;
    public Boundary boundary = Boundary.Unset;
    public Railway railway = Railway.UNSET;
    public Natural natural = Natural.UNSET;
    public Landuse landuse = Landuse.UNSET;
    public Building building = Building.UNSET;
    public Leisure leisure = Leisure.UNSET;
    public Amenity amenity = Amenity.UNSET;
    public AdminLevel adminLevel = AdminLevel.UNSET;
    public Place place = Place.UNSET;
    public string? name = null;
}