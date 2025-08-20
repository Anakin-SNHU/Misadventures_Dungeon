using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
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
    public int minRoomW = 5;
    public int maxRoomW = 9;
    public int minRoomH = 3;
    public int maxRoomH = 4;
    public int minRoomD = 5;
    public int maxRoomD = 9;
    public int minRoomY = 1;     // clamp room Y range
    public int maxRoomY = 6;     // must be < sizeY
    public int roomGap = 1;      // buffer so rooms never touch

    [Header("Graph")]
    public int kNearest = 3;
    [Range(0f, 1f)] public float extraEdgeChance = 0.15f;

    [Header("Corridors / Meander")]
    [Range(0f, 1.5f)] public float meanderStrength = 0.6f;
    [Min(1)] public int maxSlopeRun = 5;
    [Min(0)] public int corridorRadius = 0;
    [Min(0.0001f)] public float noiseScale = 0.12f;
    [Range(0f, 10f)] public float noiseWeight = 3.5f;
    public int maxPathSearch = 50000;

    [Header("Hotkeys")]
    public KeyCode regenerate = KeyCode.G;

    public VoxelDungeonConfig Clone() { return (VoxelDungeonConfig)this.MemberwiseClone(); }
}
