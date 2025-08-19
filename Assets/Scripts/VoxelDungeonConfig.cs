using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class VoxelDungeonConfig
{
    [Header("Seed and Grid")]
    public int seed = 0;                 // 0 => random each run
    public int sizeX = 48;
    public int sizeY = 8;
    public int sizeZ = 48;
    public float cellSize = 2f;

    [Header("Rooms")]
    public int roomCount = 12;
    public Vector2Int roomSizeRangeXZ = new Vector2Int(3, 8); // width/depth in cells
    public int maxPlacementTries = 1000;
    public int minRoomY = 1;     // clamp room Y range
    public int maxRoomY = 6;     // must be < sizeY
    public int roomGap = 1;      // keep this buffer so rooms never touch

    [Header("Graph")]
    public int kNearest = 3;
    [Range(0f, 1f)] public float extraEdgeChance = 0.15f;

    [Header("Hotkeys")]
    public KeyCode regenerate = KeyCode.G;
    public KeyCode levelUp = KeyCode.L;

    public VoxelDungeonConfig Clone() { return (VoxelDungeonConfig)this.MemberwiseClone(); }
}
