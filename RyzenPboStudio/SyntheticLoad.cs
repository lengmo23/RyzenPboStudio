using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace RyzenPboStudio;

/// <summary>Self-developed single-core load replacing Cinebench R23: Embree-style BVH8 ray tracing on AVX2 + FMA.
/// Each ray walks a three-level BVH8 with a stack: a node slab-tests 8 child boxes at once, hits are insertion-sorted
/// by distance and pushed; leaves run Möller–Trumbore on 8 triangles at once. Stack, sort and address dependencies
/// interleave with SIMD work like a real renderer. On a 9950X pure BVH traversal is ~0.95× R23's effective load and
/// brute-force testing of the 64 first-level nodes is ~1.10×; the two are mixed by time share to match R23.
/// No AVX-512 (R23 does not use it, it changes current and clock behaviour, and Zen3 lacks it).
/// Rays are fixed per frame, so a checksum mismatch against the reference counts as one computation error.</summary>
internal sealed class SyntheticLoad : IDisposable
{
    // 512 leaves = 8³ fill a three-level BVH8 exactly: root 1 + level-2 8 + level-1 64 nodes.
    // Leaves 512 × 288 B + nodes 73 × 224 B ≈ 160 KB, fits L2 on all three generations (Zen3 has 512 KB).
    private const int LeafCount = 512;
    private const int NodeCount = 1 + 8 + 64;
    private const int NodeStride = 6 * 8;   // min xyz, max xyz, 8 lanes each
    private const int LeafStride = 9 * 8;   // v0, e1, e2 xyz, 8 lanes each
    private const uint LeafFlag = 0x8000_0000;
    private const int RaysPerFrame = 8192;  // ~1 ms per frame; core switching and error checks are per frame

    [DllImport("kernel32")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32")]
    private static extern nuint SetThreadAffinityMask(nint hThread, nuint mask);

    private readonly float[] nodes = new float[NodeCount * NodeStride];
    private readonly uint[] children = new uint[NodeCount * 8];   // child indices, leaves tagged with LeafFlag
    private readonly float[] leaves = new float[LeafCount * LeafStride];
    private readonly uint[] stackNode = new uint[32];   // max depth 7 + 7 + 8 = 22
    private readonly float[] stackT = new float[32];
    private readonly Thread thread;
    private readonly int heavy;
    private int flatRays;   // rays per frame that take the brute-force path, derived from heavy
    private volatile bool stop;
    private long targetMask, errors;

    public static bool IsSupported => Avx2.IsSupported && Fma.IsSupported;

    /// <summary>Computation errors counted since the reference checksum was established.</summary>
    public long Errors => Interlocked.Read(ref errors);

    /// <param name="heavy">Time share (%) of brute-force testing, the rest is BVH traversal. 0 = pure BVH (lightest), 100 = pure brute force (heaviest).</param>
    public SyntheticLoad(ulong mask, int heavy)
    {
        if (!IsSupported) throw new NotSupportedException("CPU 不支持 AVX2 / FMA");
        BuildScene();
        this.heavy = heavy;
        targetMask = (long)mask;
        thread = new Thread(Loop) { IsBackground = true, Name = "SyntheticLoad" };
        thread.Start();
    }

    /// <summary>Moves the load to another logical CPU, effective from the next frame.</summary>
    public void SetTarget(ulong mask) => Interlocked.Exchange(ref targetMask, (long)mask);

    private void Loop()
    {
        long applied = 0;
        void Apply()
        {
            long want = Interlocked.Read(ref targetMask);
            if (want == applied) return;
            SetThreadAffinityMask(GetCurrentThread(), (nuint)want);
            applied = want;
        }

        // The path mix changes the checksum, so fix the brute-force ray count before taking the reference.
        // Require two identical frames so a faulty first frame cannot flag every later frame as an error.
        Apply();
        flatRays = FlatRaysFor(heavy);
        ulong reference = RunFrame();
        while (!stop)
        {
            ulong s = RunFrame();
            if (s == reference) break;
            reference = s;
        }

        while (!stop)
        {
            Apply();
            if (RunFrame() != reference) Interlocked.Increment(ref errors);
        }
    }

    /// <summary>Converts the time share into a ray count from measured costs; a brute-force ray costs over twice a BVH ray.
    /// With per-ray costs cf / cb, pick F so that F·cf / (F·cf + (N−F)·cb) = pct%. Both costs are measured through
    /// RunFrame itself, so the result does not depend on JIT state or the current clock.</summary>
    private int FlatRaysFor(int pct)
    {
        if (pct <= 0) return 0;
        if (pct >= 100) return RaysPerFrame;
        flatRays = 0;
        double cb = MinFrameSeconds();
        flatRays = RaysPerFrame;
        double cf = MinFrameSeconds();
        double p = pct / 100.0;
        return (int)Math.Round(p * RaysPerFrame * cb / (cf * (1 - p) + p * cb));
    }

    private double MinFrameSeconds()
    {
        double best = double.MaxValue;
        for (int i = 0; i < 4; i++)   // take the minimum to drop JIT and scheduling outliers
        {
            long t0 = Stopwatch.GetTimestamp();
            RunFrame();
            best = Math.Min(best, (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency);
        }
        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Mix(uint x)
    {
        x ^= x >> 16; x *= 0x7FEB352Du;
        x ^= x >> 15; x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }

    private static float Unit(uint h) => (h >> 8) * (1f / 16777216f);   // [0, 1)

    /// <summary>Cell centre offset along one axis: bit `axis` of cell selects the negative or positive half.</summary>
    private static float Offset(int cell, int axis, float half) => ((cell >> axis) & 1) == 0 ? -half : half;

    /// <summary>[-1, 1]³ split twice as an octree, each small cell holding 8 clusters of 8 triangles (~0.16 wide).
    /// The BVH is that octree, so node boxes are tight and traversal actually prunes. Fixed seed, identical every run.</summary>
    private void BuildScene()
    {
        uint s = 0x9E3779B9u;
        float Rand(float lo, float hi) { s = Mix(s + 0x6D2B79F5u); return lo + Unit(s) * (hi - lo); }

        var boxes = new float[LeafCount * 6];   // min xyz, max xyz of each child at the current level
        Span<float> v = stackalloc float[9];    // three vertices of one triangle
        for (int leaf = 0; leaf < LeafCount; leaf++)
        {
            int a = leaf >> 6, b = (leaf >> 3) & 7;   // level-2 cell, level-1 cell
            float cx = Offset(a, 0, 0.5f) + Offset(b, 0, 0.25f) + Rand(-0.15f, 0.15f);
            float cy = Offset(a, 1, 0.5f) + Offset(b, 1, 0.25f) + Rand(-0.15f, 0.15f);
            float cz = Offset(a, 2, 0.5f) + Offset(b, 2, 0.25f) + Rand(-0.15f, 0.15f);

            Span<float> box = boxes.AsSpan(leaf * 6, 6);
            box[0] = box[1] = box[2] = float.MaxValue;
            box[3] = box[4] = box[5] = float.MinValue;

            for (int tri = 0; tri < 8; tri++)
            {
                for (int k = 0; k < 9; k++) v[k] = (k % 3 == 0 ? cx : k % 3 == 1 ? cy : cz) + Rand(-0.08f, 0.08f);
                for (int k = 0; k < 9; k++)
                {
                    box[k % 3] = MathF.Min(box[k % 3], v[k]);
                    box[3 + k % 3] = MathF.Max(box[3 + k % 3], v[k]);
                }

                int lb = leaf * LeafStride + tri;
                for (int c = 0; c < 3; c++)
                {
                    leaves[lb + c * 8] = v[c];                     // v0
                    leaves[lb + (3 + c) * 8] = v[3 + c] - v[c];    // e1 = v1 - v0
                    leaves[lb + (6 + c) * 8] = v[6 + c] - v[c];    // e2 = v2 - v0
                }
            }
        }

        // Bottom-up: level-1 nodes 9..72 hold leaves, level-2 nodes 1..8 hold level 1, root 0 holds level 2
        int childStart = 0;
        bool childIsLeaf = true;
        foreach (var (start, count) in new[] { (9, 64), (1, 8), (0, 1) })
        {
            var parent = new float[count * 6];
            for (int p = 0; p < count; p++)
            {
                int node = start + p;
                Span<float> pb = parent.AsSpan(p * 6, 6);
                pb[0] = pb[1] = pb[2] = float.MaxValue;
                pb[3] = pb[4] = pb[5] = float.MinValue;

                for (int lane = 0; lane < 8; lane++)
                {
                    int child = p * 8 + lane;
                    for (int c = 0; c < 6; c++)
                    {
                        float x = boxes[child * 6 + c];
                        nodes[node * NodeStride + c * 8 + lane] = x;
                        pb[c] = c < 3 ? MathF.Min(pb[c], x) : MathF.Max(pb[c], x);
                    }
                    children[node * 8 + lane] = childIsLeaf ? LeafFlag | (uint)child : (uint)(childStart + child);
                }
            }
            boxes = parent;
            childStart = start;
            childIsLeaf = false;
        }
    }

    /// <summary>Runs one frame and returns its checksum. Rays are hashed from their index, so a healthy core always returns the same value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private ulong RunFrame()
    {
        ulong h = 14695981039346656037UL;   // FNV-1a
        int flat = flatRays, acc = 0;
        for (int i = 0; i < RaysPerFrame; i++)
        {
            uint r = Mix((uint)i * 0x9E3779B9u + 1);
            float ox = Unit(r) * 2 - 1; r = Mix(r);
            float oy = Unit(r) * 2 - 1; r = Mix(r);
            const float oz = -3f;
            float dx = Unit(r) * 2 - 1 - ox; r = Mix(r);
            float dy = Unit(r) * 2 - 1 - oy; r = Mix(r);
            float dz = Unit(r) * 2 - 1 - oz;
            float inv = 1f / MathF.Sqrt(dx * dx + dy * dy + dz * dz);

            // Spread brute-force rays evenly through the frame instead of alternating in millisecond blocks
            acc += flat;
            bool useFlat = acc >= RaysPerFrame;
            if (useFlat) acc -= RaysPerFrame;

            float t;
            int hit = useFlat
                ? TraceFlat(ox, oy, oz, dx * inv, dy * inv, dz * inv, out t)
                : Trace(ox, oy, oz, dx * inv, dy * inv, dz * inv, out t);

            h = (h ^ (uint)hit) * 1099511628211UL;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(t)) * 1099511628211UL;
        }
        return h;
    }

    /// <summary>Brute force: slab-test all 64 level-1 nodes without the tree and intersect hit leaves immediately.
    /// Almost no branches or stack work, back-to-back FMA, higher power density than BVH. Same result as Trace.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int TraceFlat(float ox, float oy, float oz, float dx, float dy, float dz, out float tBest)
    {
        ref float nb = ref MemoryMarshal.GetArrayDataReference(nodes);
        float ix = 1f / dx, iy = 1f / dy, iz = 1f / dz;
        var vix = Vector256.Create(ix); var oix = Vector256.Create(ox * ix);
        var viy = Vector256.Create(iy); var oiy = Vector256.Create(oy * iy);
        var viz = Vector256.Create(iz); var oiz = Vector256.Create(oz * iz);

        float best = float.MaxValue;
        int hit = -1;
        for (int n = 0; n < 64; n++)
        {
            nuint b = (nuint)((9 + n) * NodeStride);
            var ax = Fma.MultiplySubtract(Vector256.LoadUnsafe(ref nb, b), vix, oix);
            var ay = Fma.MultiplySubtract(Vector256.LoadUnsafe(ref nb, b + 8), viy, oiy);
            var az = Fma.MultiplySubtract(Vector256.LoadUnsafe(ref nb, b + 16), viz, oiz);
            var bx = Fma.MultiplySubtract(Vector256.LoadUnsafe(ref nb, b + 24), vix, oix);
            var by = Fma.MultiplySubtract(Vector256.LoadUnsafe(ref nb, b + 32), viy, oiy);
            var bz = Fma.MultiplySubtract(Vector256.LoadUnsafe(ref nb, b + 40), viz, oiz);

            var tmin = Avx.Max(Avx.Max(Avx.Min(ax, bx), Avx.Min(ay, by)), Avx.Max(Avx.Min(az, bz), Vector256<float>.Zero));
            var tmax = Avx.Min(Avx.Min(Avx.Max(ax, bx), Avx.Max(ay, by)), Avx.Min(Avx.Max(az, bz), Vector256.Create(best)));

            uint m = (uint)Avx.MoveMask(Avx.CompareLessThanOrEqual(tmin, tmax));
            while (m != 0)
            {
                int leaf = n * 8 + BitOperations.TrailingZeroCount(m);
                m &= m - 1;
                IntersectLeaf(leaf, ox, oy, oz, dx, dy, dz, ref best, ref hit);
            }
        }
        tBest = best;
        return hit;
    }

    /// <summary>Closest-hit traversal. The stack top is always the nearest pending node; subtrees whose tmin is not below the current best are skipped.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int Trace(float ox, float oy, float oz, float dx, float dy, float dz, out float tBest)
    {
        ref float nb = ref MemoryMarshal.GetArrayDataReference(nodes);
        float ix = 1f / dx, iy = 1f / dy, iz = 1f / dz;
        var vix = Vector256.Create(ix); var oix = Vector256.Create(ox * ix);
        var viy = Vector256.Create(iy); var oiy = Vector256.Create(oy * iy);
        var viz = Vector256.Create(iz); var oiz = Vector256.Create(oz * iz);

        uint[] sn = stackNode;
        float[] st = stackT;
        int sp = 0;
        sn[sp] = 0; st[sp] = 0; sp++;

        float best = float.MaxValue;
        int hit = -1;
        while (sp > 0)
        {
            sp--;
            if (st[sp] >= best) continue;
            uint node = sn[sp];
            if ((node & LeafFlag) != 0)
            {
                IntersectLeaf((int)(node & ~LeafFlag), ox, oy, oz, dx, dy, dz, ref best, ref hit);
                continue;
            }

            nuint b = (nuint)(node * NodeStride);
            // slab: t = (bound - o) / d = bound × (1/d) - o × (1/d)
            var ax = Fma.MultiplySubtract(Vector256.LoadUnsafe(ref nb, b), vix, oix);
            var ay = Fma.MultiplySubtract(Vector256.LoadUnsafe(ref nb, b + 8), viy, oiy);
            var az = Fma.MultiplySubtract(Vector256.LoadUnsafe(ref nb, b + 16), viz, oiz);
            var bx = Fma.MultiplySubtract(Vector256.LoadUnsafe(ref nb, b + 24), vix, oix);
            var by = Fma.MultiplySubtract(Vector256.LoadUnsafe(ref nb, b + 32), viy, oiy);
            var bz = Fma.MultiplySubtract(Vector256.LoadUnsafe(ref nb, b + 40), viz, oiz);

            var tmin = Avx.Max(Avx.Max(Avx.Min(ax, bx), Avx.Min(ay, by)), Avx.Max(Avx.Min(az, bz), Vector256<float>.Zero));
            var tmax = Avx.Min(Avx.Min(Avx.Max(ax, bx), Avx.Max(ay, by)), Avx.Min(Avx.Max(az, bz), Vector256.Create(best)));

            // Insertion-sort hit children by tmin; within [start, sp) nearer ones sit closer to the top
            uint m = (uint)Avx.MoveMask(Avx.CompareLessThanOrEqual(tmin, tmax));
            int start = sp;
            while (m != 0)
            {
                int lane = BitOperations.TrailingZeroCount(m);
                m &= m - 1;
                float tc = tmin.GetElement(lane);
                int j = sp++;
                while (j > start && st[j - 1] < tc) { st[j] = st[j - 1]; sn[j] = sn[j - 1]; j--; }
                st[j] = tc;
                sn[j] = children[node * 8 + lane];
            }
        }
        tBest = best;
        return hit;
    }

    /// <summary>Möller–Trumbore on the 8 triangles of one leaf, updating best / hit on a closer hit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void IntersectLeaf(int leaf, float ox, float oy, float oz, float dxs, float dys, float dzs,
                               ref float best, ref int hit)
    {
        ref float lb = ref MemoryMarshal.GetArrayDataReference(leaves);
        nuint b = (nuint)(leaf * LeafStride);
        var v0x = Vector256.LoadUnsafe(ref lb, b);      var v0y = Vector256.LoadUnsafe(ref lb, b + 8);  var v0z = Vector256.LoadUnsafe(ref lb, b + 16);
        var e1x = Vector256.LoadUnsafe(ref lb, b + 24); var e1y = Vector256.LoadUnsafe(ref lb, b + 32); var e1z = Vector256.LoadUnsafe(ref lb, b + 40);
        var e2x = Vector256.LoadUnsafe(ref lb, b + 48); var e2y = Vector256.LoadUnsafe(ref lb, b + 56); var e2z = Vector256.LoadUnsafe(ref lb, b + 64);
        var dx = Vector256.Create(dxs); var dy = Vector256.Create(dys); var dz = Vector256.Create(dzs);

        // p = d × e2
        var px = Fma.MultiplySubtract(dy, e2z, Avx.Multiply(dz, e2y));
        var py = Fma.MultiplySubtract(dz, e2x, Avx.Multiply(dx, e2z));
        var pz = Fma.MultiplySubtract(dx, e2y, Avx.Multiply(dy, e2x));
        var det = Fma.MultiplyAdd(e1x, px, Fma.MultiplyAdd(e1y, py, Avx.Multiply(e1z, pz)));
        var inv = Avx.Divide(Vector256.Create(1f), det);

        var sx = Avx.Subtract(Vector256.Create(ox), v0x);
        var sy = Avx.Subtract(Vector256.Create(oy), v0y);
        var sz = Avx.Subtract(Vector256.Create(oz), v0z);
        var u = Avx.Multiply(Fma.MultiplyAdd(sx, px, Fma.MultiplyAdd(sy, py, Avx.Multiply(sz, pz))), inv);

        // q = s × e1
        var qx = Fma.MultiplySubtract(sy, e1z, Avx.Multiply(sz, e1y));
        var qy = Fma.MultiplySubtract(sz, e1x, Avx.Multiply(sx, e1z));
        var qz = Fma.MultiplySubtract(sx, e1y, Avx.Multiply(sy, e1x));
        var v = Avx.Multiply(Fma.MultiplyAdd(dx, qx, Fma.MultiplyAdd(dy, qy, Avx.Multiply(dz, qz))), inv);
        var t = Avx.Multiply(Fma.MultiplyAdd(e2x, qx, Fma.MultiplyAdd(e2y, qy, Avx.Multiply(e2z, qz))), inv);

        var eps = Vector256.Create(1e-7f);
        var zero = Vector256<float>.Zero;
        var ok = Avx.CompareGreaterThan(Avx.AndNot(Vector256.Create(-0f), det), eps);   // |det| > eps
        ok = Avx.And(ok, Avx.CompareGreaterThanOrEqual(u, zero));
        ok = Avx.And(ok, Avx.CompareGreaterThanOrEqual(v, zero));
        ok = Avx.And(ok, Avx.CompareLessThanOrEqual(Avx.Add(u, v), Vector256.Create(1f)));
        ok = Avx.And(ok, Avx.CompareGreaterThan(t, eps));
        ok = Avx.And(ok, Avx.CompareLessThan(t, Vector256.Create(best)));

        uint m = (uint)Avx.MoveMask(ok);
        while (m != 0)
        {
            int lane = BitOperations.TrailingZeroCount(m);
            m &= m - 1;
            float tl = t.GetElement(lane);
            if (tl < best) { best = tl; hit = leaf * 8 + lane; }
        }
    }

    public void Dispose()
    {
        stop = true;
        thread.Join(2000);
    }
}
