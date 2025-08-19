using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class VoxelDungeonGenerator : MonoBehaviour
{
    [Header("Config and Prefabs")]
    public VoxelDungeonConfig baseConfig;
    public Transform root;
    public GameObject roomPrefab;
    public GameObject corridorPrefab;
    public Material matRoom;
    public Material matCorridor;
    public Material matStart;
    public Material matExit;

    [Header("Runtime")]
    public int currentLevel = 1;
    public int lastSeed;

    private System.Random rng;
    private VoxelDungeonConfig cfg;

    private bool[,,] roomGrid;
    private bool[,,] corGrid;
    private int sx, sy, sz;

    private class Room
    {
        public int id;
        public RectInt rectXZ;  // XZ on the grid
        public int y;
        public List<Vector3Int> cells = new List<Vector3Int>();
        public Vector3Int CenterCell => new Vector3Int(rectXZ.x + rectXZ.width / 2, y, rectXZ.y + rectXZ.height / 2);
        public List<Vector3Int> DoorCandidates = new List<Vector3Int>();
    }

    private List<Room> rooms = new List<Room>();

    private struct Edge { public int a, b; }
    private List<Edge> edges = new List<Edge>();
    private int startId = -1, exitId = -1;

    void Reset()
    {
        if (baseConfig == null) baseConfig = new VoxelDungeonConfig();
    }

    void Start()
    {
        if (baseConfig == null) baseConfig = new VoxelDungeonConfig();
        Generate();
    }

    void Update()
    {
        if (Input.GetKeyDown(baseConfig.regenerate)) Generate();
        if (Input.GetKeyDown(baseConfig.levelUp))
        {
            currentLevel++;
            Generate();
        }
    }

    public void Generate()
    {
        cfg = baseConfig.Clone();

        float s = 1f + 0.25f * (currentLevel - 1);
        cfg.sizeX = Mathf.RoundToInt(cfg.sizeX * s);
        cfg.sizeZ = Mathf.RoundToInt(cfg.sizeZ * s);
        cfg.roomCount = Mathf.RoundToInt(Mathf.Max(2, cfg.roomCount * s));

        lastSeed = (cfg.seed != 0) ? cfg.seed : Random.Range(1, int.MaxValue);
        rng = new System.Random(lastSeed);

        sx = cfg.sizeX; sy = cfg.sizeY; sz = cfg.sizeZ;
        roomGrid = new bool[sx, sy, sz];
        corGrid = new bool[sx, sy, sz];
        rooms.Clear(); edges.Clear(); startId = exitId = -1;

        if (root == null) root = this.transform;
        ClearChildren(root);

        PlaceRooms();
        BuildGraph();
        ChooseStartExit();
        CarveCorridors();
        BuildMeshes();
    }

    void ClearChildren(Transform t)
    {
        var kill = new List<GameObject>();
        foreach (Transform c in t) kill.Add(c.gameObject);
        foreach (var go in kill)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(go);
            else Destroy(go);
#else
            Destroy(go);
#endif
        }
    }

    // ----------------- ROOM PLACEMENT -----------------
    void PlaceRooms()
    {
        int tries = 0;
        int nextId = 0;

        int minY = Mathf.Clamp(cfg.minRoomY, 0, sy - 1);
        int maxY = Mathf.Clamp(cfg.maxRoomY, 0, sy - 1);
        if (maxY < minY) maxY = minY;

        while (rooms.Count < cfg.roomCount && tries < cfg.maxPlacementTries)
        {
            tries++;

            int w = rng.Next(cfg.roomSizeRangeXZ.x, cfg.roomSizeRangeXZ.y + 1);
            int d = rng.Next(cfg.roomSizeRangeXZ.x, cfg.roomSizeRangeXZ.y + 1);

            if (w + 2 * cfg.roomGap + 2 > sx || d + 2 * cfg.roomGap + 2 > sz) continue;

            int x = rng.Next(cfg.roomGap, sx - w - cfg.roomGap);
            int z = rng.Next(cfg.roomGap, sz - d - cfg.roomGap);
            int y = rng.Next(minY, maxY + 1);

            // FIX: use correct RectInt ctor (x, y, width, height)
            var rect = new RectInt(x, z, w, d);

            if (OverlapsAny(rect, y, cfg.roomGap)) continue;

            var r = new Room { id = nextId++, rectXZ = rect, y = y };

            for (int ix = rect.x; ix < rect.x + rect.width; ix++)
                for (int iz = rect.y; iz < rect.y + rect.height; iz++)
                {
                    roomGrid[ix, y, iz] = true;
                    r.cells.Add(new Vector3Int(ix, y, iz));
                }

            // perimeter door candidates
            for (int ix = rect.x; ix < rect.x + rect.width; ix++)
            {
                r.DoorCandidates.Add(new Vector3Int(ix, y, rect.y));
                r.DoorCandidates.Add(new Vector3Int(ix, y, rect.y + rect.height - 1));
            }
            for (int iz = rect.y; iz < rect.y + rect.height; iz++)
            {
                r.DoorCandidates.Add(new Vector3Int(rect.x, y, iz));
                r.DoorCandidates.Add(new Vector3Int(rect.x + rect.width - 1, y, iz));
            }

            rooms.Add(r);
        }
    }

    bool OverlapsAny(RectInt rect, int y, int gap)
    {
        int x0 = Mathf.Max(0, rect.x - gap);
        int z0 = Mathf.Max(0, rect.y - gap);
        int x1 = Mathf.Min(sx - 1, rect.x + rect.width + gap - 1);
        int z1 = Mathf.Min(sz - 1, rect.y + rect.height + gap - 1);

        for (int ix = x0; ix <= x1; ix++)
            for (int iz = z0; iz <= z1; iz++)
                if (roomGrid[ix, y, iz]) return true;

        return false;
    }

    // ----------------- GRAPH -----------------
    void BuildGraph()
    {
        if (rooms.Count < 2) return;

        var cand = new HashSet<(int, int)>();
        foreach (var r in rooms)
        {
            var nearest = rooms.Where(o => o.id != r.id)
                // FIX: use integer squared distance
                .OrderBy(o => SqrDist(o.CenterCell, r.CenterCell))
                .Take(cfg.kNearest);

            foreach (var n in nearest)
            {
                int a = Mathf.Min(r.id, n.id);
                int b = Mathf.Max(r.id, n.id);
                cand.Add((a, b));
            }
        }

        // MST (Prim)
        var connected = new HashSet<int> { rooms[0].id };
        var candList = cand.ToList();
        var mst = new List<Edge>();

        while (connected.Count < rooms.Count)
        {
            float best = float.MaxValue;
            (int, int) pick = (-1, -1);

            foreach (var e in candList)
            {
                bool aIn = connected.Contains(e.Item1);
                bool bIn = connected.Contains(e.Item2);
                if (aIn == bIn) continue;

                var ra = rooms.First(x => x.id == e.Item1);
                var rb = rooms.First(x => x.id == e.Item2);
                // FIX: Vector3 distance with cast
                float cost = Vector3.Distance((Vector3)ra.CenterCell, (Vector3)rb.CenterCell);
                if (cost < best) { best = cost; pick = e; }
            }
            if (pick.Item1 == -1) break;

            mst.Add(new Edge { a = pick.Item1, b = pick.Item2 });
            connected.Add(pick.Item1);
            connected.Add(pick.Item2);
        }

        // optional extra edges for loops
        var extra = cand.Except(mst.Select(e => (Mathf.Min(e.a, e.b), Mathf.Max(e.a, e.b))));
        foreach (var e in extra)
            if (rng.NextDouble() < cfg.extraEdgeChance)
                edges.Add(new Edge { a = e.Item1, b = e.Item2 });

        // ensure connectivity
        edges.AddRange(mst);
    }

    // ----------------- START/EXIT -----------------
    void ChooseStartExit()
    {
        if (rooms.Count < 2) return;

        var adj = new Dictionary<int, List<int>>();
        foreach (var r in rooms) adj[r.id] = new List<int>();
        foreach (var e in edges) { adj[e.a].Add(e.b); adj[e.b].Add(e.a); }

        int farA = Farthest(rooms[0].id, adj);
        int farB = Farthest(farA, adj);
        startId = farA; exitId = farB;
    }

    int Farthest(int start, Dictionary<int, List<int>> adj)
    {
        var q = new Queue<int>();
        var dist = new Dictionary<int, int>();
        foreach (var k in adj.Keys) dist[k] = int.MaxValue;
        dist[start] = 0; q.Enqueue(start);
        int best = start;

        while (q.Count > 0)
        {
            int u = q.Dequeue();
            foreach (var v in adj[u])
            {
                if (dist[v] != int.MaxValue) continue;
                dist[v] = dist[u] + 1; q.Enqueue(v);
                if (dist[v] > dist[best]) best = v;
            }
        }
        return best;
    }

    // ----------------- CORRIDORS (A* on voxel grid) -----------------
    void CarveCorridors()
    {
        foreach (var e in edges)
        {
            var ra = rooms.First(r => r.id == e.a);
            var rb = rooms.First(r => r.id == e.b);

            var aDoor = ClosestDoorCell(ra, rb.CenterCell);
            var bDoor = ClosestDoorCell(rb, ra.CenterCell);

            var path = FindPathAStar(aDoor, bDoor);
            if (path == null) continue;

            foreach (var p in path)
            {
                if (!roomGrid[p.x, p.y, p.z])
                    corGrid[p.x, p.y, p.z] = true;
            }
        }
    }

    Vector3Int ClosestDoorCell(Room r, Vector3Int target)
    {
        Vector3Int best = r.DoorCandidates[0];
        int bestD = int.MaxValue;
        foreach (var d in r.DoorCandidates)
        {
            int dd = SqrDist(d, target);
            if (dd < bestD) { bestD = dd; best = d; }
        }
        return best;
    }

    List<Vector3Int> FindPathAStar(Vector3Int start, Vector3Int goal)
    {
        var open = new PriorityQueue<Vector3Int>();
        var came = new Dictionary<Vector3Int, Vector3Int>();
        var g = new Dictionary<Vector3Int, int>();
        var f = new Dictionary<Vector3Int, int>();

        open.Enqueue(start, 0);
        g[start] = 0; f[start] = Heuristic(start, goal);

        var visited = new HashSet<Vector3Int>();

        while (open.Count > 0)
        {
            var current = open.Dequeue();
            if (current == goal) return Reconstruct(came, current);

            if (visited.Contains(current)) continue;
            visited.Add(current);

            foreach (var n in Neighbors(current))
            {
                if (!InBounds(n)) continue;

                if (roomGrid[n.x, n.y, n.z] && n != start && n != goal) continue;

                int tentative = g[current] + 1;
                int prev;
                if (!g.TryGetValue(n, out prev) || tentative < prev)
                {
                    came[n] = current;
                    g[n] = tentative;
                    int h = Heuristic(n, goal);
                    f[n] = tentative + h;
                    open.Enqueue(n, f[n]);
                }
            }
        }
        return null;
    }

    IEnumerable<Vector3Int> Neighbors(Vector3Int c)
    {
        yield return new Vector3Int(c.x + 1, c.y, c.z);
        yield return new Vector3Int(c.x - 1, c.y, c.z);
        yield return new Vector3Int(c.x, c.y, c.z + 1);
        yield return new Vector3Int(c.x, c.y, c.z - 1);
        yield return new Vector3Int(c.x, c.y + 1, c.z);
        yield return new Vector3Int(c.x, c.y - 1, c.z);
    }

    bool InBounds(Vector3Int p)
    {
        return p.x >= 0 && p.x < sx && p.y >= 0 && p.y < sy && p.z >= 0 && p.z < sz;
    }

    int Heuristic(Vector3Int a, Vector3Int b)
    {
        var d = new Vector3Int(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z));
        return d.x + d.y + d.z; // Manhattan
    }

    List<Vector3Int> Reconstruct(Dictionary<Vector3Int, Vector3Int> came, Vector3Int cur)
    {
        var path = new List<Vector3Int> { cur };
        while (came.ContainsKey(cur))
        {
            cur = came[cur];
            path.Add(cur);
        }
        path.Reverse();
        return path;
    }

    // ----------------- BUILD -----------------
    void BuildMeshes()
    {
        float s = cfg.cellSize;

        foreach (var r in rooms)
        {
            var mat = (r.id == startId) ? matStart : (r.id == exitId) ? matExit : matRoom;
            foreach (var c in r.cells)
                SpawnCube(roomPrefab, mat, CellToWorld(c), s);
        }

        for (int x = 0; x < sx; x++)
            for (int y = 0; y < sy; y++)
                for (int z = 0; z < sz; z++)
                    if (corGrid[x, y, z])
                        SpawnCube(corridorPrefab, matCorridor, CellToWorld(new Vector3Int(x, y, z)), s);
    }

    Vector3 CellToWorld(Vector3Int cell)
    {
        return new Vector3(cell.x * cfg.cellSize, cell.y * cfg.cellSize, cell.z * cfg.cellSize);
    }

    void SpawnCube(GameObject prefab, Material mat, Vector3 pos, float cell)
    {
        var go = Instantiate(prefab, root);
        go.transform.position = pos;
        go.transform.localScale = new Vector3(cell, cell, cell);
        var mr = go.GetComponent<MeshRenderer>();
        if (mr && mat) mr.sharedMaterial = mat;
    }

    // ----------------- UTILS -----------------
    int SqrDist(Vector3Int a, Vector3Int b)
    {
        int dx = a.x - b.x;
        int dy = a.y - b.y;
        int dz = a.z - b.z;
        return dx * dx + dy * dy + dz * dz;
    }

    private class PriorityQueue<T>
    {
        private readonly List<(T item, int pri)> heap = new List<(T, int)>();
        public int Count => heap.Count;

        public void Enqueue(T item, int pri)
        {
            heap.Add((item, pri));
            SiftUp(heap.Count - 1);
        }

        public T Dequeue()
        {
            var top = heap[0].item;
            heap[0] = heap[heap.Count - 1];
            heap.RemoveAt(heap.Count - 1);
            if (heap.Count > 0) SiftDown(0);
            return top;
        }

        void SiftUp(int i)
        {
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (heap[i].pri >= heap[p].pri) break;
                (heap[i], heap[p]) = (heap[p], heap[i]);
                i = p;
            }
        }

        void SiftDown(int i)
        {
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, s = i;
                if (l < heap.Count && heap[l].pri < heap[s].pri) s = l;
                if (r < heap.Count && heap[r].pri < heap[s].pri) s = r;
                if (s == i) break;
                (heap[i], heap[s]) = (heap[s], heap[i]);
                i = s;
            }
        }
    }
}
