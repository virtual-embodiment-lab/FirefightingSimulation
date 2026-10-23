using UnityEngine;

public class HouseLayoutData : MonoBehaviour
{
    [Header("Layout Information")]
    public string layoutName = "Custom House Layout";
    public int seed;

    [System.Serializable] // makes it so we can adjust/save these in the Inspector
    public struct HouseSettings
    {
        // Should contain every variable that we are able to adjust during RoomGeneration...
        public int maxHouseWidth;
        public int maxHouseLength;
        public int minRoomWidth;
        public int maxRoomWidth;
        public int minRoomLength;
        public int maxRoomLength;
        public int wallHeight;
        public int numberOfRooms;
        public int numberOfFloors;
        public bool identicalFloors;
        public bool roomAmountsDifferPerFloor;
        public bool heightenedNooks;
        public bool generateHallways;
        public bool spineHallways;
        public float hallwayChance;
        public int hallwayWidth;
        public int minComplexity;
        public int maxComplexity;
        public bool allowVoidSpaces;
        public int minViableWidth;
        public int minViableLength;
        public float windowChance;
        public float doorHeight;
        public float windowHeight;
        public float windowWidth;
        public int windowSpacing;
        public bool generateYard;
        public int minYardPadding;
        public int maxYardPadding;
        public bool chanceForOutbuilding;
        public float outbuildingSpawnChance;
        public int outbuildingOffset;
        public Material floorMaterial;
        public Material wallMaterial;
        public Material ceilingMaterial;
        public Material yardMaterial;
    }

    [HideInInspector]
    public HouseSettings settings; // holds them all

    // Allows us to grab the settings of a particular layout that's been passed along to us, given its gen info and seed
    public void CaptureSettings(RoomGeneration gen, int layoutSeed, string defaultName)
    {
        seed = layoutSeed;
        if (string.IsNullOrEmpty(layoutName) || layoutName == "Custom House Layout")
            layoutName = defaultName;

        settings = new HouseSettings
        {
            maxHouseWidth = gen.maxHouseWidth,
            maxHouseLength = gen.maxHouseLength,
            minRoomWidth = gen.minRoomWidth,
            maxRoomWidth = gen.maxRoomWidth,
            minRoomLength = gen.minRoomLength,
            maxRoomLength = gen.maxRoomLength,
            wallHeight = gen.wallHeight,
            numberOfRooms = gen.numberOfRooms,
            numberOfFloors = gen.numberOfFloors,
            identicalFloors = gen.identicalFloors,
            roomAmountsDifferPerFloor = gen.roomAmountsDifferPerFloor,
            heightenedNooks = gen.heightenedNooks,
            generateHallways = gen.generateHallways,
            spineHallways = gen.spineHallways,
            hallwayChance = gen.hallwayChance,
            hallwayWidth = gen.hallwayWidth,
            minComplexity = gen.minComplexity,
            maxComplexity = gen.maxComplexity,
            allowVoidSpaces = gen.allowVoidSpaces,
            minViableWidth = gen.minViableWidth,
            minViableLength = gen.minViableLength,
            windowChance = gen.windowChance,
            doorHeight = gen.doorHeight,
            windowHeight = gen.windowHeight,
            windowWidth = gen.windowWidth,
            windowSpacing = gen.windowSpacing,
            generateYard = gen.generateYard,
            minYardPadding = gen.minYardPadding,
            maxYardPadding = gen.maxYardPadding,
            chanceForOutbuilding = gen.chanceForOutbuilding,
            outbuildingSpawnChance = gen.outbuildingSpawnChance,
            outbuildingOffset = gen.outbuildingOffset,
            floorMaterial = gen.floorMaterial,
            wallMaterial = gen.wallMaterial,
            ceilingMaterial = gen.ceilingMaterial,
            yardMaterial = gen.yardMaterial
        };
    }

    // Passes all the necessary numbers and logic to make a particular house layout into the generation object
    public void ApplySettings(RoomGeneration gen)
    {
        gen.maxHouseWidth = settings.maxHouseWidth;
        gen.maxHouseLength = settings.maxHouseLength;
        gen.minRoomWidth = settings.minRoomWidth;
        gen.maxRoomWidth = settings.maxRoomWidth;
        gen.minRoomLength = settings.minRoomLength;
        gen.maxRoomLength = settings.maxRoomLength;
        gen.wallHeight = settings.wallHeight;
        gen.numberOfRooms = settings.numberOfRooms;
        gen.numberOfFloors = settings.numberOfFloors;
        gen.identicalFloors = settings.identicalFloors;
        gen.roomAmountsDifferPerFloor = settings.roomAmountsDifferPerFloor;
        gen.heightenedNooks = settings.heightenedNooks;
        gen.generateHallways = settings.generateHallways;
        gen.spineHallways = settings.spineHallways;
        gen.hallwayChance = settings.hallwayChance;
        gen.hallwayWidth = settings.hallwayWidth;
        gen.minComplexity = settings.minComplexity;
        gen.maxComplexity = settings.maxComplexity;
        gen.allowVoidSpaces = settings.allowVoidSpaces;
        gen.minViableWidth = settings.minViableWidth;
        gen.minViableLength = settings.minViableLength;
        gen.windowChance = settings.windowChance;
        gen.doorHeight = settings.doorHeight;
        gen.windowHeight = settings.windowHeight;
        gen.windowWidth = settings.windowWidth;
        gen.windowSpacing = settings.windowSpacing;
        gen.generateYard = settings.generateYard;
        gen.minYardPadding = settings.minYardPadding;
        gen.maxYardPadding = settings.maxYardPadding;
        gen.chanceForOutbuilding = settings.chanceForOutbuilding;
        gen.outbuildingSpawnChance = settings.outbuildingSpawnChance;
        gen.outbuildingOffset = settings.outbuildingOffset;
        gen.floorMaterial = settings.floorMaterial;
        gen.wallMaterial = settings.wallMaterial;
        gen.ceilingMaterial = settings.ceilingMaterial;
        gen.yardMaterial = settings.yardMaterial;
    }
}
