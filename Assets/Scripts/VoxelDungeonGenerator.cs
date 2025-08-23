using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class VoxelDungeonGenerator : MonoBehaviour
{
    [System.Serializable]
    public class Config
    {
        [Header("Seed and Grid")]
        public int seed = 0;                                // Procedural seed; 0 selects a new random seed each generation.
        public int sizeX = 30;                              // Grid size along the X axis, in cells.
        public int sizeY = 10;                              // Grid size along the Y axis (layers), in cells.
        public int sizeZ = 30;                              // Grid size along the Z axis, in cells.
        [Min(0.1f)] public float cellSize = 1f;            // World units represented by a single grid cell.

        [Header("Rooms")]
        public int roomCount = 10;                          // Target number of rooms to attempt to place.
        public Vector2Int roomSizeRangeXZ = new Vector2Int(3, 8); // Inclusive width/depth range (cells) used for random room dimensions.
        public int minRoomY = 1;                            // Minimum room height (cells), inclusive.
        public int maxRoomY = 4;                            // Maximum room height (cells), inclusive.
        public int maxPlacementTries = 1000;                // Upper bound on random placement attempts before stopping.
        public int roomGap = 1;                             // Empty-cell buffer maintained around each room to prevent touching.

        [Header("Graph")]
        [Tooltip("How many nearest neighbors to consider when building the room graph.")]
        [Range(1, 8)] public int nearest = 3;               // K-nearest value for candidate edges between rooms.
        [Tooltip("Chance to add an extra non-MST edge to create loops.")]
        [Range(0f, 1f)] public float extraEdgeChance = 0.15f; // Probability of adding additional edges beyond the spanning tree.

        [Header("Prefabs/Materials")]
        public GameObject roomPrefab;                       // Prefab spawned per room cell.
        public GameObject corridorPrefab;                   // Prefab spawned per corridor cell.
        public Material startMat;                           // Material applied to the start room’s cells.
        public Material endMat;                             // Material applied to the end room’s cells.
        public Material entranceMat;                        // Material applied to single doorway cells on rooms.
        public Material corridorMat;                        // Material applied to corridor instances.

        [Header("Bounds Overlay (Game View)")]
        public bool drawBounds = true;                      // Enables a runtime wireframe showing the generator’s total bounds.
        public Color boundsColor = new Color(0f, 1f, 0f, 0.5f); // Color/alpha used by the bounds wire.
        public float boundsLineWidth = 0.02f;               // LineRenderer width for the bounds wire.
    }

    public Config config = new Config();                    // Authoring configuration exposed in the inspector.

    // Hotkeys
    [Header("Hotkeys")]
    public KeyCode regenerateKey = KeyCode.G;               // Triggers a full regeneration.
    public KeyCode selectAxisXKey = KeyCode.Z;              // Selects X for size adjustment.
    public KeyCode selectAxisYKey = KeyCode.X;              // Selects Y for size adjustment.
    public KeyCode selectAxisZKey = KeyCode.C;              // Selects Z for size adjustment.
    public KeyCode sizeIncreaseKey = KeyCode.UpArrow;       // Increases the selected dimension.
    public KeyCode sizeDecreaseKey = KeyCode.DownArrow;     // Decreases the selected dimension.
    public int sizeStep = 10;                               // Magnitude of each size adjustment step (cells).

    enum Axis { X, Y, Z }                                   // Axis selector for interactive resizing.
    Axis selectedAxis = Axis.X;                             // Currently selected axis for resizing.

    // Internal state
    System.Random rng;                                      // Deterministic RNG derived from the seed.
    bool[,,] grid;                                          // Occupancy map; true for any filled cell (room or corridor).
    bool[,,] isRoom;                                        // Room membership map; true for cells belonging to rooms.
    HashSet<Vector3Int> doors = new HashSet<Vector3Int>();  // One doorway cell recorded per room.
    List<Room> rooms = new List<Room>();                    // Placed rooms with cached metadata.

    Transform root;                                         // Parent transform for all generated content.
    Transform roomRoot;                                     // Parent for room cell instances.
    Transform corridorRoot;                                 // Parent for corridor cell instances.
    LineRenderer boundsLR;                                   // Runtime bounds wire drawn in the Game view.

    struct Room
    {
        public BoundsInt bounds;                            // Inclusive-exclusive bounds (grid-space) for the room volume.
        public Vector3Int center;                           // Integer center of the room (grid-space).
        public Vector3Int door;                             // Selected doorway cell on the room perimeter.
        public int index;                                   // Stable index assigned at placement.
    }

    void Awake()
    {
        EnsureRoots();                                      // Lazily create/locate container transforms and the bounds wire.
    }

    void Start()
    {
        Generate();                                         // Build the initial dungeon on startup.
    }

    void Update()
    {
        if (Input.GetKeyDown(selectAxisXKey)) selectedAxis = Axis.X;   // Switch selected axis: X.
        if (Input.GetKeyDown(selectAxisYKey)) selectedAxis = Axis.Y;   // Switch selected axis: Y.
        if (Input.GetKeyDown(selectAxisZKey)) selectedAxis = Axis.Z;   // Switch selected axis: Z.

        if (Input.GetKeyDown(sizeIncreaseKey)) { BumpAxis(+sizeStep); Generate(); } // Grow selected dimension and rebuild.
        if (Input.GetKeyDown(sizeDecreaseKey)) { BumpAxis(-sizeStep); Generate(); } // Shrink selected dimension and rebuild.

        if (Input.GetKeyDown(regenerateKey)) Generate();    // Regenerate with current settings.
    }

    void BumpAxis(int delta)
    {
        switch (selectedAxis)
        {
            case Axis.X: config.sizeX = Mathf.Max(1, config.sizeX + delta); break; // Clamp to at least 1 cell.
            case Axis.Y: config.sizeY = Mathf.Max(1, config.sizeY + delta); break; // Clamp to at least 1 cell.
            case Axis.Z: config.sizeZ = Mathf.Max(1, config.sizeZ + delta); break; // Clamp to at least 1 cell.
        }
    }

    void EnsureRoots()
    {
        // Create or reuse a "Generated" root under this component.
        if (root == null)
        {
            var existing = transform.Find("Generated");
            root = existing != null ? existing : new GameObject("Generated").transform;
            root.SetParent(transform, false);
        }
        // Create or reuse "Rooms" and "Corridors" children to organize spawned geometry.
        if (roomRoot == null)
        {
            var existing = root.Find("Rooms");
            roomRoot = existing != null ? existing : new GameObject("Rooms").transform;
            roomRoot.SetParent(root, false);
        }
        if (corridorRoot == null)
        {
            var existing = root.Find("Corridors");
            corridorRoot = existing != null ? existing : new GameObject("Corridors").transform;
            corridorRoot.SetParent(root, false);
        }
        // Create or reuse a LineRenderer to visualize generator bounds at runtime.
        if (boundsLR == null)
        {
            var existing = root.Find("BoundsWire");
            GameObject go = existing ? existing.gameObject : new GameObject("BoundsWire");
            if (!existing) go.transform.SetParent(root, false);
            boundsLR = go.GetComponent<LineRenderer>();
            if (boundsLR == null) boundsLR = go.AddComponent<LineRenderer>();
            SetupBoundsLR();                                // Initialize renderer properties.
        }
    }

    void SetupBoundsLR()
    {
        boundsLR.useWorldSpace = true;                      // Positions are authored in world-space.
        boundsLR.loop = false;                              // Polyline assembled from discrete segments.
        boundsLR.widthMultiplier = config.boundsLineWidth;  // Width configured by authoring settings.
        boundsLR.material = new Material(Shader.Find("Sprites/Default")); // Simple unlit material for line rendering.
        boundsLR.startColor = config.boundsColor;           // Uniform color across the line.
        boundsLR.endColor = config.boundsColor;
        boundsLR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // Avoids unnecessary overhead.
        boundsLR.receiveShadows = false;
        boundsLR.enabled = config.drawBounds;               // Respect visibility toggle.
    }

    public void Generate()
    {
        EnsureRoots();                                      // Ensure hierarchy exists prior to spawning.
        ClearChildren(roomRoot);                            // Remove previously generated rooms.
        ClearChildren(corridorRoot);                        // Remove previously generated corridors.

        rng = new System.Random(                            // Initialize RNG from fixed or random seed.
            config.seed == 0 ? Random.Range(int.MinValue, int.MaxValue) : config.seed);

        grid = new bool[config.sizeX, config.sizeY, config.sizeZ]; // Clear occupancy map.
        isRoom = new bool[config.sizeX, config.sizeY, config.sizeZ]; // Clear room map.
        rooms.Clear();                                      // Reset room list.
        doors.Clear();                                      // Reset door registry.

        PlaceRoomsNoOverlap();                              // Randomly place disjoint rooms with separation.
        ChooseOneDoorPerRoom();                             // Pick one perimeter cell to designate as a doorway.
        ConnectRoomsWithCorridors();                        // Build graph connections and carve corridors.
        SpawnGeometry();                                    // Instantiate prefabs for occupied cells.
        ColorStartAndEnd();                                 // Mark two farthest rooms with start/end materials.
        BuildBoundsWire();                                  // Update runtime bounds wire for visualization.
    }

    void PlaceRoomsNoOverlap()
    {
        int tries = 0;                                      // Placement attempts performed so far.
        while (rooms.Count < config.roomCount && tries < config.maxPlacementTries)
        {
            tries++;

            int w = RandomRangeInclusive(config.roomSizeRangeXZ.x, config.roomSizeRangeXZ.y); // Random width.
            int d = RandomRangeInclusive(config.roomSizeRangeXZ.x, config.roomSizeRangeXZ.y); // Random depth.
            int h = RandomRangeInclusive(config.minRoomY, config.maxRoomY);                   // Random height.

            int gap = config.roomGap;                      // Buffer region to keep rooms separated.
            int maxX = Mathf.Max(1, config.sizeX - w - gap);
            int maxY = Mathf.Max(1, config.sizeY - h - gap);
            int maxZ = Mathf.Max(1, config.sizeZ - d - gap);
            if (maxX <= gap || maxY <= gap || maxZ <= gap) break; // Early exit if no valid placement remains.

            int x = rng.Next(gap, maxX);                   // Random origin within valid range.
            int y = rng.Next(gap, maxY);
            int z = rng.Next(gap, maxZ);

            var b = new BoundsInt(x, y, z, w, h, d);       // Candidate room bounds (grid-space).
            if (OverlapsWithGap(b, gap)) continue;         // Skip if overlapping another room including gap.

            var room = new Room
            {
                bounds = b,
                center = new Vector3Int(b.x + b.size.x / 2, b.y + b.size.y / 2, b.z + b.size.z / 2),
                door = Vector3Int.zero,                    // Placeholder; assigned in ChooseOneDoorPerRoom.
                index = rooms.Count
            };
            rooms.Add(room);

            foreach (var c in Cells(b))                    // Mark grid occupancy for the placed room.
            {
                isRoom[c.x, c.y, c.z] = true;
                grid[c.x, c.y, c.z] = true;
            }
        }
    }

    int RandomRangeInclusive(int a, int b)
    {
        if (a > b) (a, b) = (b, a);                        // Normalize bounds if provided out of order.
        return rng.Next(a, b + 1);                          // System.Random upper bound is exclusive.
    }

    bool OverlapsWithGap(BoundsInt candidate, int gap)
    {
        // Tests candidate against all placed rooms with an expanded AABB that includes the configured gap.
        foreach (var r in rooms)
        {
            var a = candidate;
            var b = r.bounds;

            var bMin = new Vector3Int(b.xMin - gap, b.yMin - gap, b.zMin - gap);
            var bMax = new Vector3Int(b.xMax + gap, b.yMax + gap, b.zMax + gap);
            if (a.xMin < bMax.x && a.xMax > bMin.x &&
                a.yMin < bMax.y && a.yMax > bMin.y &&
                a.zMin < bMax.z && a.zMax > bMin.z)
            {
                return true;                                // Overlap (including buffer) detected.
            }
        }
        return false;
    }

    void ChooseOneDoorPerRoom()
    {
        // For each room, collect perimeter cells that border non-room space and pick one uniformly at random.
        for (int i = 0; i < rooms.Count; i++)
        {
            var r = rooms[i];
            var perim = new List<Vector3Int>();

            foreach (var c in Cells(r.bounds))
            {
                bool onPerimeter =
                    c.x == r.bounds.xMin || c.x == r.bounds.xMax - 1 ||
                    c.y == r.bounds.yMin || c.y == r.bounds.yMax - 1 ||
                    c.z == r.bounds.zMin || c.z == r.bounds.zMax - 1;

                if (!onPerimeter) continue;

                foreach (var n in Neighbors6(c))
                {
                    if (!InBounds(n)) continue;
                    if (!isRoom[n.x, n.y, n.z]) { perim.Add(c); break; } // Perimeter cell adjacent to empty space.
                }
            }

            Vector3Int door = r.center;                     // Fallback if no perimeter candidate exists.
            if (perim.Count > 0) door = perim[rng.Next(perim.Count)];

            r.door = door;                                  // Persist selection.
            rooms[i] = r;
            doors.Add(door);
        }
    }

    void ConnectRoomsWithCorridors()
    {
        if (rooms.Count <= 1) return;                       // Nothing to connect.

        // Build candidate edges from K-nearest neighbors based on center-to-center distance.
        var edges = new List<(int a, int b, float w)>();
        for (int i = 0; i < rooms.Count; i++)
        {
            List<(int j, float dist)> neigh = new List<(int, float)>();
            for (int j = 0; j < rooms.Count; j++)
            {
                if (i == j) continue;
                float d = Vector3Int.Distance(rooms[i].center, rooms[j].center);
                neigh.Add((j, d));
            }
            neigh.Sort((u, v) => u.dist.CompareTo(v.dist));
            int take = Mathf.Min(config.nearest, neigh.Count);
            for (int k = 0; k < take; k++)
            {
                int j = neigh[k].j;
                if (i < j) edges.Add((i, j, neigh[k].dist)); // Keep each undirected edge once.
            }
        }

        edges.Sort((e1, e2) => e1.w.CompareTo(e2.w));       // Sort by distance to prefer shorter connections.
        var uf = new UnionFind(rooms.Count);                // Disjoint-set for Kruskal’s algorithm.
        var chosen = new List<(int a, int b)>();            // Final edge set to realize as corridors.

        foreach (var e in edges)
        {
            if (uf.Union(e.a, e.b)) chosen.Add((e.a, e.b)); // Minimum spanning forest.
        }
        foreach (var e in edges)
        {
            if (Random.value < config.extraEdgeChance) chosen.Add((e.a, e.b)); // Optional cycles for more loops.
        }

        // For each selected connection, carve a BFS path between door cells, avoiding room interiors.
        foreach (var (a, b) in chosen)
        {
            Vector3Int start = rooms[a].door;
            Vector3Int goal = rooms[b].door;

            var path = BFSPath(start, goal, blockRooms: true);
            if (path == null) continue;

            foreach (var p in path)
            {
                if (!isRoom[p.x, p.y, p.z]) grid[p.x, p.y, p.z] = true; // Mark corridor cells without overwriting rooms.
            }
        }
    }

    List<Vector3Int> BFSPath(Vector3Int start, Vector3Int goal, bool blockRooms)
    {
        // Standard breadth-first search on a 6-connected grid with optional prohibition of room interiors.
        var q = new Queue<Vector3Int>();
        var came = new Dictionary<Vector3Int, Vector3Int>(); // Predecessor map.
        var visited = new HashSet<Vector3Int>();

        q.Enqueue(start);
        visited.Add(start);

        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (cur == goal) break;

            foreach (var n in Neighbors6(cur))
            {
                if (!InBounds(n) || visited.Contains(n)) continue;

                bool walkable = true;
                if (blockRooms)
                {
                    if (isRoom[n.x, n.y, n.z] && !doors.Contains(n)) walkable = false; // Enter rooms only via door cells.
                }

                if (walkable)
                {
                    visited.Add(n);
                    came[n] = cur;
                    q.Enqueue(n);
                }
            }
        }

        if (!came.ContainsKey(goal) && start != goal) return null; // Unreachable.

        // Reconstruct path from goal to start (inclusive), then reverse.
        var path = new List<Vector3Int>();
        var t = goal;
        path.Add(t);
        while (t != start)
        {
            if (!came.ContainsKey(t)) return path;          // Incomplete path when goal equals start or reached early.
            t = came[t];
            path.Add(t);
        }
        path.Reverse();
        return path;
    }

    void SpawnGeometry()
    {
        // Spawn room instances and highlight door cells using the entrance material when provided.
        for (int i = 0; i < rooms.Count; i++)
        {
            var r = rooms[i];
            foreach (var c in Cells(r.bounds))
            {
                var go = Instantiate(config.roomPrefab, GridToWorld(c), Quaternion.identity, roomRoot);
                if (c == r.door && config.entranceMat != null) ApplyMat(go, config.entranceMat);
            }
        }

        // Spawn corridor instances for occupied non-room cells (and avoid double-spawning door cells).
        for (int x = 0; x < config.sizeX; x++)
            for (int y = 0; y < config.sizeY; y++)
                for (int z = 0; z < config.sizeZ; z++)
                {
                    if (!grid[x, y, z]) continue;
                    if (isRoom[x, y, z] && !doors.Contains(new Vector3Int(x, y, z))) continue; // Skip interior room cells.

                    if (doors.Contains(new Vector3Int(x, y, z)) && isRoom[x, y, z]) continue; // Skip doorway already spawned as a room cell.

                    var go = Instantiate(config.corridorPrefab, GridToWorld(new Vector3Int(x, y, z)), Quaternion.identity, corridorRoot);
                    if (config.corridorMat != null) ApplyMat(go, config.corridorMat);
                }
    }

    void ColorStartAndEnd()
    {
        // Identify the pair of rooms with maximum center distance and apply start/end materials.
        if (rooms.Count == 0) return;
        int a = 0, b = 0;
        float best = -1f;
        for (int i = 0; i < rooms.Count; i++)
            for (int j = i + 1; j < rooms.Count; j++)
            {
                float d = Vector3Int.Distance(rooms[i].center, rooms[j].center);
                if (d > best) { best = d; a = i; b = j; }
            }

        ColorRoom(roomRoot, rooms[a], config.startMat);
        ColorRoom(roomRoot, rooms[b], config.endMat);
    }

    void ColorRoom(Transform parent, Room r, Material m)
    {
        if (m == null) return;
        var cells = new HashSet<Vector3Int>();
        foreach (var c in Cells(r.bounds)) cells.Add(c);

        // Iterate spawned instances under the room parent and recolor any whose grid position falls within the room.
        foreach (Transform t in parent)
        {
            var g = WorldToGrid(t.position);
            if (cells.Contains(g)) ApplyMat(t.gameObject, m);
        }
    }

    void ApplyMat(GameObject go, Material m)
    {
        var r = go.GetComponentInChildren<Renderer>();
        if (r != null) r.sharedMaterial = m;               // Use sharedMaterial for editor-time batching and reduced instantiation.
    }

    IEnumerable<Vector3Int> Cells(BoundsInt b)
    {
        // Iterates all integer positions within the inclusive-exclusive bounds.
        for (int x = b.xMin; x < b.xMax; x++)
            for (int y = b.yMin; y < b.yMax; y++)
                for (int z = b.zMin; z < b.zMax; z++)
                    yield return new Vector3Int(x, y, z);
    }

    Vector3 GridToWorld(Vector3Int c)
        => transform.TransformPoint(new Vector3(c.x * config.cellSize, c.y * config.cellSize, c.z * config.cellSize)); // Converts grid cell to world-space.

    Vector3Int WorldToGrid(Vector3 w)
    {
        // Converts a world position to the nearest integer grid coordinate in this generator’s local space.
        var local = transform.InverseTransformPoint(w);
        return new Vector3Int(
            Mathf.RoundToInt(local.x / config.cellSize),
            Mathf.RoundToInt(local.y / config.cellSize),
            Mathf.RoundToInt(local.z / config.cellSize)
        );
    }

    bool InBounds(Vector3Int c)
        => c.x >= 0 && c.x < config.sizeX &&
           c.y >= 0 && c.y < config.sizeY &&
           c.z >= 0 && c.z < config.sizeZ;                 // Tests whether a grid coordinate lies within the active volume.

    IEnumerable<Vector3Int> Neighbors6(Vector3Int c)
    {
        // 6-connected neighbors (axis-aligned adjacency).
        yield return new Vector3Int(c.x + 1, c.y, c.z);
        yield return new Vector3Int(c.x - 1, c.y, c.z);
        yield return new Vector3Int(c.x, c.y + 1, c.z);
        yield return new Vector3Int(c.x, c.y - 1, c.z);
        yield return new Vector3Int(c.x, c.y, c.z + 1);
        yield return new Vector3Int(c.x, c.y, c.z - 1);
    }

    void ClearChildren(Transform t)
    {
        // Destroys all immediate children under the given parent (editor and play mode).
        for (int i = t.childCount - 1; i >= 0; i--)
            DestroyImmediate(t.GetChild(i).gameObject);
    }

    // ========= Runtime Bounds Wire (Game view) =========
    void BuildBoundsWire()
    {
        if (boundsLR == null) EnsureRoots();
        SetupBoundsLR();
        boundsLR.enabled = config.drawBounds;

        if (!config.drawBounds)
        {
            boundsLR.positionCount = 0;                     // Hide the line when disabled.
            return;
        }

        // Compute world-space corners for the generator AABB starting at this transform’s origin.
        Vector3 min = transform.position;
        Vector3 max = transform.position + new Vector3(config.sizeX * config.cellSize,
                                                       config.sizeY * config.cellSize,
                                                       config.sizeZ * config.cellSize);

        Vector3[] c = new Vector3[8];                       // Corner ordering: bottom ring 0..3, top ring 4..7.
        c[0] = new Vector3(min.x, min.y, min.z);
        c[1] = new Vector3(max.x, min.y, min.z);
        c[2] = new Vector3(max.x, min.y, max.z);
        c[3] = new Vector3(min.x, min.y, max.z);
        c[4] = new Vector3(min.x, max.y, min.z);
        c[5] = new Vector3(max.x, max.y, min.z);
        c[6] = new Vector3(max.x, max.y, max.z);
        c[7] = new Vector3(min.x, max.y, max.z);

        // Assemble a polyline that explicitly draws all 12 edges (duplicate endpoints avoid diagonal connections).
        var pts = new List<Vector3>(24);
        // bottom rectangle
        pts.Add(c[0]); pts.Add(c[1]);
        pts.Add(c[1]); pts.Add(c[2]);
        pts.Add(c[2]); pts.Add(c[3]);
        pts.Add(c[3]); pts.Add(c[0]);
        // top rectangle
        pts.Add(c[4]); pts.Add(c[5]);
        pts.Add(c[5]); pts.Add(c[6]);
        pts.Add(c[6]); pts.Add(c[7]);
        pts.Add(c[7]); pts.Add(c[4]);
        // verticals
        pts.Add(c[0]); pts.Add(c[4]);
        pts.Add(c[1]); pts.Add(c[5]);
        pts.Add(c[2]); pts.Add(c[6]);
        pts.Add(c[3]); pts.Add(c[7]);

        boundsLR.positionCount = pts.Count;                 // Upload vertex positions to the LineRenderer.
        boundsLR.SetPositions(pts.ToArray());
        boundsLR.widthMultiplier = config.boundsLineWidth;
        boundsLR.startColor = config.boundsColor;
        boundsLR.endColor = config.boundsColor;
    }

    // Simple union-find
    class UnionFind
    {
        int[] p; int[] r;                                   // Parent and rank arrays.
        public UnionFind(int n) { p = new int[n]; r = new int[n]; for (int i = 0; i < n; i++) { p[i] = i; r[i] = 0; } }
        int Find(int x) { return p[x] == x ? x : (p[x] = Find(p[x])); } // Path compression.
        public bool Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a == b) return false;                       // Already in the same set.
            if (r[a] < r[b]) p[a] = b;
            else if (r[a] > r[b]) p[b] = a;
            else { p[b] = a; r[a]++; }
            return true;                                    // Merge succeeded.
        }
    }
}
