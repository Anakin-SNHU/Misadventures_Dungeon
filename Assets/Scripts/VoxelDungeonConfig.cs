using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class VoxelDungeonConfig
{
    /// <summary>
    /// Configuration for a voxel-based dungeon generator; values are expressed in grid cells unless noted.
    /// </summary>

    [Header("Seed and Grid")]
    public int seed = 0;                 // Seed for procedural generation; 0 selects a new random seed each run.
    public int sizeX = 48;               // Grid size along X in cells.
    public int sizeY = 8;                // Grid size along Y in cells (vertical layers).
    public int sizeZ = 48;               // Grid size along Z in cells.
    public float cellSize = 2f;          // World-space size of a single grid cell (units per cell).

    [Header("Rooms")]
    public int roomCount = 12;           // Target number of rooms to place within the grid.
    public int minRoomW = 5;             // Minimum room width in cells (inclusive).
    public int maxRoomW = 9;             // Maximum room width in cells (inclusive).
    public int minRoomH = 3;             // Minimum room height in cells (inclusive).
    public int maxRoomH = 4;             // Maximum room height in cells (inclusive).
    public int minRoomD = 5;             // Minimum room depth in cells (inclusive).
    public int maxRoomD = 9;             // Maximum room depth in cells (inclusive).
    public int minRoomY = 1;             // Lowest Y layer a room may occupy (clamped to grid bounds).
    public int maxRoomY = 6;             // Highest Y layer a room may occupy; must be strictly less than sizeY.
    public int roomGap = 1;              // Number of empty cells kept between rooms to prevent adjacency.

    [Header("Graph")]
    public int kNearest = 3;             // Number of nearest neighbors per room when building the connectivity graph.
    [Range(0f, 1f)] public float extraEdgeChance = 0.15f; // Probability of adding an extra random edge between rooms (0–1).

    [Header("Corridors / Meander")]
    [Range(0f, 1.5f)] public float meanderStrength = 0.6f; // Lateral deviation applied when carving corridors; higher increases winding.
    [Min(1)] public int maxSlopeRun = 5;                   // Maximum consecutive vertical steps allowed before forcing a level segment.
    [Min(0)] public int corridorRadius = 0;                // Additional radius around the path to carve for thicker corridors (in cells).
    [Min(0.0001f)] public float noiseScale = 0.12f;        // Spatial scale of the noise field influencing pathing (smaller = finer detail).
    [Range(0f, 10f)] public float noiseWeight = 3.5f;      // Strength of the noise field’s influence on corridor direction.
    public int maxPathSearch = 50000;                      // Upper bound on pathfinding node expansions to guard against runaway searches.

    [Header("Hotkeys")]
    public KeyCode regenerate = KeyCode.G; // Key used to trigger a full dungeon regeneration at runtime/editor.

    public VoxelDungeonConfig Clone()      // Creates a shallow copy suitable for isolated mutation during generation.
    {
        return (VoxelDungeonConfig)this.MemberwiseClone();
    }
}
