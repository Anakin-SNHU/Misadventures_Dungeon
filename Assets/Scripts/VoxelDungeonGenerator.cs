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
        public int seed = 0;
        public int sizeX = 30;
        public int sizeY = 10;
        public int sizeZ = 30;
        [Min(0.1f)] public float cellSize = 1f;

        [Header("Rooms")]
        public int roomCount = 10;
        public Vector2Int roomSizeRangeXZ = new Vector2Int(3, 8); // inclusive
        public int minRoomY = 1;
        public int maxRoomY = 4;
        public int maxPlacementTries = 1000;
        public int roomGap = 1; // keep this many empty cells between rooms

        [Header("Graph")]
        [Tooltip("How many nearest neighbors to consider when building the room graph.")]
        [Range(1, 8)] public int nearest = 3;
        [Tooltip("Chance to add an extra non-MST edge to create loops.")]
        [Range(0f, 1f)] public float extraEdgeChance = 0.15f;

        [Header("Prefabs/Materials")]
        public GameObject roomPrefab;
        public GameObject corridorPrefab;
        public Material startMat;
        public Material endMat;
        public Material entranceMat; // color for doorway tiles on rooms
        public Material corridorMat;

        [Header("Bounds Overlay (Game View)")]
        public bool drawBounds = true;
        public Color boundsColor = new Color(0f, 1f, 0f, 0.5f);
        public float boundsLineWidth = 0.02f;
    }

    public Config config = new Config();

    // Hotkeys
    [Header("Hotkeys")]
    public KeyCode regenerateKey = KeyCode.G;
    public KeyCode selectAxisXKey = KeyCode.Z;
    public KeyCode selectAxisYKey = KeyCode.X;
    public KeyCode selectAxisZKey = KeyCode.C;
    public KeyCode sizeIncreaseKey = KeyCode.UpArrow;
    public KeyCode sizeDecreaseKey = KeyCode.DownArrow;
    public int sizeStep = 10;

    enum Axis { X, Y, Z }
    Axis selectedAxis = Axis.X;

    // Internal
    System.Random rng;
    bool[,,] grid;                  // true = room or corridor is placed here
    bool[,,] isRoom;               // true = cell belongs to a room
    HashSet<Vector3Int> doors = new HashSet<Vector3Int>(); // the one door per room
    List<Room> rooms = new List<Room>();

    Transform root;
    Transform roomRoot;
    Transform corridorRoot;
    LineRenderer boundsLR;   // RUNTIME bounds wire (Game view)

    struct Room
    {
        public BoundsInt bounds;
        public Vector3Int center;     // integer center (grid)
        public Vector3Int door;       // single doorway cell on the room perimeter
        public int index;
    }

    void Awake()
    {
        EnsureRoots();
    }

    void Start()
    {
        Generate();
    }

    void Update()
    {
        if (Input.GetKeyDown(selectAxisXKey)) selectedAxis = Axis.X;
        if (Input.GetKeyDown(selectAxisYKey)) selectedAxis = Axis.Y;
        if (Input.GetKeyDown(selectAxisZKey)) selectedAxis = Axis.Z;

        if (Input.GetKeyDown(sizeIncreaseKey)) { BumpAxis(+sizeStep); Generate(); }
        if (Input.GetKeyDown(sizeDecreaseKey)) { BumpAxis(-sizeStep); Generate(); }

        if (Input.GetKeyDown(regenerateKey)) Generate();
    }

    void BumpAxis(int delta)
    {
        switch (selectedAxis)
        {
            case Axis.X: config.sizeX = Mathf.Max(1, config.sizeX + delta); break;
            case Axis.Y: config.sizeY = Mathf.Max(1, config.sizeY + delta); break;
            case Axis.Z: config.sizeZ = Mathf.Max(1, config.sizeZ + delta); break;
        }
    }

    void EnsureRoots()
    {
        if (root == null)
        {
            var existing = transform.Find("Generated");
            root = existing != null ? existing : new GameObject("Generated").transform;
            root.SetParent(transform, false);
        }
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
        // Bounds child (LineRenderer)
        if (boundsLR == null)
        {
            var existing = root.Find("BoundsWire");
            GameObject go = existing ? existing.gameObject : new GameObject("BoundsWire");
            if (!existing) go.transform.SetParent(root, false);
            boundsLR = go.GetComponent<LineRenderer>();
            if (boundsLR == null) boundsLR = go.AddComponent<LineRenderer>();
            SetupBoundsLR();
        }
    }

    void SetupBoundsLR()
    {
        boundsLR.useWorldSpace = true;
        boundsLR.loop = false;
        boundsLR.widthMultiplier = config.boundsLineWidth;
        boundsLR.material = new Material(Shader.Find("Sprites/Default"));
        boundsLR.startColor = config.boundsColor;
        boundsLR.endColor = config.boundsColor;
        boundsLR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        boundsLR.receiveShadows = false;
        boundsLR.enabled = config.drawBounds;
    }

    public void Generate()
    {
        EnsureRoots();
        ClearChildren(roomRoot);
        ClearChildren(corridorRoot);

        rng = new System.Random(config.seed == 0 ? Random.Range(int.MinValue, int.MaxValue) : config.seed);

        grid = new bool[config.sizeX, config.sizeY, config.sizeZ];
        isRoom = new bool[config.sizeX, config.sizeY, config.sizeZ];
        rooms.Clear();
        doors.Clear();

        PlaceRoomsNoOverlap();
        ChooseOneDoorPerRoom();
        ConnectRoomsWithCorridors();
        SpawnGeometry();
        ColorStartAndEnd();
        BuildBoundsWire();   // <<< runtime cage for Game view
    }

    void PlaceRoomsNoOverlap()
    {
        int tries = 0;
        while (rooms.Count < config.roomCount && tries < config.maxPlacementTries)
        {
            tries++;

            int w = RandomRangeInclusive(config.roomSizeRangeXZ.x, config.roomSizeRangeXZ.y);
            int d = RandomRangeInclusive(config.roomSizeRangeXZ.x, config.roomSizeRangeXZ.y);
            int h = RandomRangeInclusive(config.minRoomY, config.maxRoomY);

            int gap = config.roomGap;
            int maxX = Mathf.Max(1, config.sizeX - w - gap);
            int maxY = Mathf.Max(1, config.sizeY - h - gap);
            int maxZ = Mathf.Max(1, config.sizeZ - d - gap);
            if (maxX <= gap || maxY <= gap || maxZ <= gap) break;

            int x = rng.Next(gap, maxX);
            int y = rng.Next(gap, maxY);
            int z = rng.Next(gap, maxZ);

            var b = new BoundsInt(x, y, z, w, h, d);
            if (OverlapsWithGap(b, gap)) continue;

            var room = new Room
            {
                bounds = b,
                center = new Vector3Int(b.x + b.size.x / 2, b.y + b.size.y / 2, b.z + b.size.z / 2),
                door = Vector3Int.zero,
                index = rooms.Count
            };
            rooms.Add(room);

            foreach (var c in Cells(b))
            {
                isRoom[c.x, c.y, c.z] = true;
                grid[c.x, c.y, c.z] = true;
            }
        }
    }

    int RandomRangeInclusive(int a, int b)
    {
        if (a > b) (a, b) = (b, a);
        return rng.Next(a, b + 1);
    }

    bool OverlapsWithGap(BoundsInt candidate, int gap)
    {
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
                return true;
            }
        }
        return false;
    }

    void ChooseOneDoorPerRoom()
    {
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
                    if (!isRoom[n.x, n.y, n.z]) { perim.Add(c); break; }
                }
            }

            Vector3Int door = r.center;
            if (perim.Count > 0) door = perim[rng.Next(perim.Count)];

            r.door = door;
            rooms[i] = r;
            doors.Add(door);
        }
    }

    void ConnectRoomsWithCorridors()
    {
        if (rooms.Count <= 1) return;

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
                if (i < j) edges.Add((i, j, neigh[k].dist));
            }
        }

        edges.Sort((e1, e2) => e1.w.CompareTo(e2.w));
        var uf = new UnionFind(rooms.Count);
        var chosen = new List<(int a, int b)>();

        foreach (var e in edges)
        {
            if (uf.Union(e.a, e.b)) chosen.Add((e.a, e.b));
        }
        foreach (var e in edges)
        {
            if (Random.value < config.extraEdgeChance) chosen.Add((e.a, e.b));
        }

        foreach (var (a, b) in chosen)
        {
            Vector3Int start = rooms[a].door;
            Vector3Int goal = rooms[b].door;

            var path = BFSPath(start, goal, blockRooms: true);
            if (path == null) continue;

            foreach (var p in path)
            {
                if (!isRoom[p.x, p.y, p.z]) grid[p.x, p.y, p.z] = true;
            }
        }
    }

    List<Vector3Int> BFSPath(Vector3Int start, Vector3Int goal, bool blockRooms)
    {
        var q = new Queue<Vector3Int>();
        var came = new Dictionary<Vector3Int, Vector3Int>();
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
                    if (isRoom[n.x, n.y, n.z] && !doors.Contains(n)) walkable = false;
                }

                if (walkable)
                {
                    visited.Add(n);
                    came[n] = cur;
                    q.Enqueue(n);
                }
            }
        }

        if (!came.ContainsKey(goal) && start != goal) return null;

        var path = new List<Vector3Int>();
        var t = goal;
        path.Add(t);
        while (t != start)
        {
            if (!came.ContainsKey(t)) return path;
            t = came[t];
            path.Add(t);
        }
        path.Reverse();
        return path;
    }

    void SpawnGeometry()
    {
        for (int i = 0; i < rooms.Count; i++)
        {
            var r = rooms[i];
            foreach (var c in Cells(r.bounds))
            {
                var go = Instantiate(config.roomPrefab, GridToWorld(c), Quaternion.identity, roomRoot);
                if (c == r.door && config.entranceMat != null) ApplyMat(go, config.entranceMat);
            }
        }

        for (int x = 0; x < config.sizeX; x++)
            for (int y = 0; y < config.sizeY; y++)
                for (int z = 0; z < config.sizeZ; z++)
                {
                    if (!grid[x, y, z]) continue;
                    if (isRoom[x, y, z] && !doors.Contains(new Vector3Int(x, y, z))) continue;

                    if (doors.Contains(new Vector3Int(x, y, z)) && isRoom[x, y, z]) continue;

                    var go = Instantiate(config.corridorPrefab, GridToWorld(new Vector3Int(x, y, z)), Quaternion.identity, corridorRoot);
                    if (config.corridorMat != null) ApplyMat(go, config.corridorMat);
                }
    }

    void ColorStartAndEnd()
    {
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

        foreach (Transform t in parent)
        {
            var g = WorldToGrid(t.position);
            if (cells.Contains(g)) ApplyMat(t.gameObject, m);
        }
    }

    void ApplyMat(GameObject go, Material m)
    {
        var r = go.GetComponentInChildren<Renderer>();
        if (r != null) r.sharedMaterial = m;
    }

    IEnumerable<Vector3Int> Cells(BoundsInt b)
    {
        for (int x = b.xMin; x < b.xMax; x++)
            for (int y = b.yMin; y < b.yMax; y++)
                for (int z = b.zMin; z < b.zMax; z++)
                    yield return new Vector3Int(x, y, z);
    }

    Vector3 GridToWorld(Vector3Int c)
        => transform.TransformPoint(new Vector3(c.x * config.cellSize, c.y * config.cellSize, c.z * config.cellSize));

    Vector3Int WorldToGrid(Vector3 w)
    {
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
           c.z >= 0 && c.z < config.sizeZ;

    IEnumerable<Vector3Int> Neighbors6(Vector3Int c)
    {
        yield return new Vector3Int(c.x + 1, c.y, c.z);
        yield return new Vector3Int(c.x - 1, c.y, c.z);
        yield return new Vector3Int(c.x, c.y + 1, c.z);
        yield return new Vector3Int(c.x, c.y - 1, c.z);
        yield return new Vector3Int(c.x, c.y, c.z + 1);
        yield return new Vector3Int(c.x, c.y, c.z - 1);
    }

    void ClearChildren(Transform t)
    {
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
            boundsLR.positionCount = 0;
            return;
        }

        // Build a polyline covering all 12 edges (pairs duplicated to avoid unwanted diagonals).
        Vector3 min = transform.position;
        Vector3 max = transform.position + new Vector3(config.sizeX * config.cellSize,
                                                       config.sizeY * config.cellSize,
                                                       config.sizeZ * config.cellSize);

        Vector3[] c = new Vector3[8];
        c[0] = new Vector3(min.x, min.y, min.z);
        c[1] = new Vector3(max.x, min.y, min.z);
        c[2] = new Vector3(max.x, min.y, max.z);
        c[3] = new Vector3(min.x, min.y, max.z);
        c[4] = new Vector3(min.x, max.y, min.z);
        c[5] = new Vector3(max.x, max.y, min.z);
        c[6] = new Vector3(max.x, max.y, max.z);
        c[7] = new Vector3(min.x, max.y, max.z);

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

        boundsLR.positionCount = pts.Count;
        boundsLR.SetPositions(pts.ToArray());
        boundsLR.widthMultiplier = config.boundsLineWidth;
        boundsLR.startColor = config.boundsColor;
        boundsLR.endColor = config.boundsColor;
    }

    // Simple union-find
    class UnionFind
    {
        int[] p; int[] r;
        public UnionFind(int n) { p = new int[n]; r = new int[n]; for (int i = 0; i < n; i++) { p[i] = i; r[i] = 0; } }
        int Find(int x) { return p[x] == x ? x : (p[x] = Find(p[x])); }
        public bool Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a == b) return false;
            if (r[a] < r[b]) p[a] = b;
            else if (r[a] > r[b]) p[b] = a;
            else { p[b] = a; r[a]++; }
            return true;
        }
    }
}
