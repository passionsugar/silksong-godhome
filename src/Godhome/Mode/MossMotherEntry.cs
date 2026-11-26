using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Godhome;

/// <summary>
/// One bounded, geometry-checked entry repair for the Weavenest pair. The configured door
/// is a preference, not proof that FindGroundPoint left the hero on the arena side of an
/// exit wall. Only the explicitly configured BattleScene supplies the allowed region.
/// No saved coordinates, guessed world positions, or recurring teleports are used.
/// </summary>
internal static class MossMotherEntry
{
    internal static bool Applies(BossDef b) => b.Id == "moss_mother_duo" && b.EntryScene == "Weave_03" && b.Scene == "Weave_03";

    private const float Skin = 0.05f;
    private const int TerrainLayer = 8;
    private const int MaxRegions = 4;
    private const int MaxColumns = 48;
    private const int MaxCandidates = 96;
    private const int MaxQueries = 512;
    private const long MaxMilliseconds = 50;

    /// <summary>
    /// Read-only planning; the caller checks fight/slot/hero ownership again before moving.
    /// Full hero bounds must fit inside a convex region, have terrain support in this exact
    /// scene instance, and clear terrain/solid bodies/active damage volumes. Failure is
    /// explicit so the caller can return to the lobby instead of leaving the hero trapped.
    /// </summary>
    internal static bool TryFind(HeroController hero, Collider2D heroCol, TransitionPoint door,
        BattleScene? battle, Vector2 preferred, out Vector2 target, out string detail)
    {
        target = hero.transform.position;
        Scene scene = door.gameObject.scene;
        if (!scene.IsValid() || !scene.isLoaded || scene.name != "Weave_03" || battle == null
            || battle.gameObject.scene.handle != scene.handle || !battle.gameObject.activeInHierarchy)
        {
            detail = "configured BattleScene is unavailable in the entry scene";
            return false;
        }
        Physics2D.SyncTransforms();
        Bounds body = heroCol.bounds;
        if (!heroCol.enabled || heroCol.isTrigger || !Finite(body.size.x) || !Finite(body.size.y)
            || body.size.x <= 0.1f || body.size.y <= 0.1f)
        {
            detail = "hero collision bounds are unavailable";
            return false;
        }
        var regions = new List<Region>();
        foreach (Collider2D c in battle.GetComponents<Collider2D>())
        {
            if (c != null && c.isTrigger && c.gameObject.activeInHierarchy && Region.Create(c) is Region r)
            {
                regions.Add(r);
            }
            if (regions.Count >= MaxRegions)
            {
                break;
            }
        }
        if (regions.Count == 0)
        {
            detail = "configured BattleScene has no supported convex trigger (box/circle/capsule/single convex polygon)";
            return false;
        }
        var search = new Search(hero, scene, (Vector2)body.size, (Vector2)body.center - target);
        regions.Sort((a, b) => Vector2.SqrMagnitude((Vector2)a.Bounds.ClosestPoint(preferred) - preferred)
            .CompareTo(Vector2.SqrMagnitude((Vector2)b.Bounds.ClosestPoint(preferred) - preferred)));
        foreach (Region r in regions)
        {
            if (r.Contains(body.center, body.extents) && search.ClearAt(body.center)
                && search.Supported(body.center, Mathf.Min(12f, body.min.y - r.Bounds.min.y + Skin)))
            {
                detail = "existing landing is inside the battle region and clear";
                return true;
            }
        }
        int columns = 0;
        int candidates = 0;
        foreach (Region r in regions)
        {
            float low = r.Bounds.min.x + body.extents.x + Skin;
            float high = r.Bounds.max.x - body.extents.x - Skin;
            if (low > high || r.Bounds.size.y < body.size.y + 2f * Skin)
            {
                continue;
            }
            // Include the preferred x exactly, then search a finite grid in nearest-first order.
            var xs = new List<float> { Mathf.Clamp(preferred.x + search.Offset.x, low, high) };
            for (int i = 0; i < MaxColumns; i++)
            {
                xs.Add(Mathf.Lerp(low, high, i / (float)(MaxColumns - 1)));
            }
            foreach (float x in xs.Distinct().OrderBy(x => Mathf.Abs(x - preferred.x - search.Offset.x)))
            {
                if (++columns > MaxColumns || search.Exhausted)
                {
                    break;
                }
                float top = r.Bounds.max.y - Skin;
                float near = Mathf.Clamp(preferred.y + body.extents.y, r.Bounds.min.y + Skin, top);
                // A high ceiling can obscure the floor; first cast from the configured entry
                // height, then from the region top. Both origins remain bounded by the region.
                foreach (float y in new[] { near, top }.Distinct())
                {
                    foreach (RaycastHit2D floor in search.Floors(new Vector2(x, y), y - r.Bounds.min.y + Skin))
                    {
                        if (++candidates > MaxCandidates || search.Exhausted)
                        {
                            break;
                        }
                        var center = new Vector2(x, floor.point.y + body.extents.y + Skin);
                        if (r.Contains(center, body.extents) && search.ClearAt(center)
                            && search.Supported(center, 3f * Skin))
                        {
                            target = center - search.Offset;
                            detail = $"recovered within configured BattleScene ({columns} columns, {candidates} candidates)";
                            return true;
                        }
                    }
                    if (candidates >= MaxCandidates || search.Exhausted)
                    {
                        break;
                    }
                }
                if (candidates >= MaxCandidates)
                {
                    break;
                }
            }
            if (columns >= MaxColumns || candidates >= MaxCandidates || search.Exhausted)
            {
                break;
            }
        }
        detail = $"no verified landing inside configured BattleScene ({columns} columns, {candidates} candidates{(search.Exhausted ? ", search budget reached" : "")})";
        return false;
    }

    private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);

    private sealed class Search
    {
        private readonly HeroController hero;
        private readonly Scene scene;
        private readonly Vector2 size;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly RaycastHit2D[] rays = new RaycastHit2D[128];
        private readonly Collider2D[] overlaps = new Collider2D[128];
        private readonly ContactFilter2D terrain = new() { useTriggers = false, useLayerMask = true, layerMask = 1 << TerrainLayer };
        private readonly ContactFilter2D all = new() { useTriggers = true };
        private int queries;
        internal Vector2 Offset { get; }
        internal bool Exhausted => queries >= MaxQueries || clock.ElapsedMilliseconds >= MaxMilliseconds;

        internal Search(HeroController hero, Scene scene, Vector2 size, Vector2 offset)
        {
            this.hero = hero;
            this.scene = scene;
            this.size = size;
            Offset = offset;
        }

        private bool Solid(Collider2D c) => c != null && c.enabled && c.gameObject.activeInHierarchy
            && c.gameObject.scene.handle == scene.handle && !c.isTrigger && c.gameObject.layer == TerrainLayer;

        internal RaycastHit2D[] Floors(Vector2 from, float distance)
        {
            if (Exhausted || distance <= 0f)
            {
                return Array.Empty<RaycastHit2D>();
            }
            queries++;
            int count = Physics2D.Raycast(from, Vector2.down, terrain, rays, distance);
            // Saturation is ambiguous; do not choose from an incomplete set of geometry hits.
            if (count >= rays.Length)
            {
                return Array.Empty<RaycastHit2D>();
            }
            return rays.Take(count).Where(h => Solid(h.collider) && h.distance > 0f && h.normal.y >= 0.8f)
                .OrderBy(h => h.distance).ToArray();
        }

        internal bool ClearAt(Vector2 center)
        {
            if (Exhausted)
            {
                return false;
            }
            queries++;
            // Permit only the thin contact strip at the feet, not side/top penetration.
            Vector2 probe = center + Vector2.up * (Skin * 0.5f);
            int count = Physics2D.OverlapBox(probe, new Vector2(size.x, size.y - Skin), 0f, all, overlaps);
            if (count >= overlaps.Length)
            {
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                Collider2D c = overlaps[i];
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy || c.gameObject.scene.handle != scene.handle
                    || c.transform.IsChildOf(hero.transform))
                {
                    continue;
                }
                DamageHero? damage = c.GetComponentInParent<DamageHero>();
                if (!c.isTrigger || (damage != null && damage.isActiveAndEnabled && damage.damageDealt > 0))
                {
                    return false;
                }
            }
            return true;
        }

        internal bool Supported(Vector2 center, float distance)
        {
            if (distance <= 0f)
            {
                return false;
            }
            float feet = center.y - size.y * 0.5f;
            // Check both sides as well as the centre: a lone ray can accept the edge of a pit.
            foreach (float x in new[] { center.x, center.x - size.x * 0.45f, center.x + size.x * 0.45f })
            {
                if (Floors(new Vector2(x, feet + Skin), distance + Skin).Length == 0)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Conservative convex world polygon. Compute from shape data, not Collider.bounds:
    /// BattleScene may already have disabled its trigger after the first enter callback.
    /// Curved triggers use inscribed polygons. Unknown/concave/multi-path shapes are rejected;
    /// four inside corners alone cannot prove containment in a concave trigger.
    /// </summary>
    private sealed class Region
    {
        private readonly Vector2[] vertices;
        internal Bounds Bounds { get; }

        private Region(Vector2[] vertices)
        {
            this.vertices = vertices;
            var bounds = new Bounds(vertices[0], Vector3.zero);
            foreach (Vector2 p in vertices)
            {
                bounds.Encapsulate(p);
            }
            Bounds = bounds;
        }

        internal static Region? Create(Collider2D c)
        {
            // A constituent shape can be cut by another collider; its original outline
            // cannot prove containment in the resulting composite trigger.
            if (c.compositeOperation != Collider2D.CompositeOperation.None)
            {
                return null;
            }
            // Physics may rebuild circle/capsule radii differently under non-uniform scale.
            // Accept curved outlines only with equal, orthogonal XY axes and no out-of-plane tilt.
            if ((c is CircleCollider2D || c is CapsuleCollider2D) && !UniformInPlane(c.transform))
            {
                return null;
            }
            var local = new List<Vector2>();
            if (c is BoxCollider2D box)
            {
                Vector2 h = box.size * 0.5f;
                local.AddRange(new[] { new Vector2(-h.x, -h.y), new Vector2(h.x, -h.y), new Vector2(h.x, h.y), new Vector2(-h.x, h.y) });
            }
            else if (c is PolygonCollider2D polygon && polygon.pathCount == 1 && polygon.GetTotalPointCount() <= 128)
            {
                local.AddRange(polygon.GetPath(0));
            }
            else if (c is CircleCollider2D circle)
            {
                for (int i = 0; i < 32; i++)
                {
                    float angle = 2f * Mathf.PI * i / 32f;
                    local.Add(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * circle.radius);
                }
            }
            else if (c is CapsuleCollider2D capsule)
            {
                bool vertical = capsule.direction == CapsuleDirection2D.Vertical;
                float radius = Mathf.Min(capsule.size.x, capsule.size.y) * 0.5f;
                float segment = Mathf.Max(0f, (vertical ? capsule.size.y : capsule.size.x) * 0.5f - radius);
                for (int i = 0; i < 32; i++)
                {
                    float angle = 2f * Mathf.PI * i / 32f;
                    Vector2 p = new(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
                    if (vertical)
                    {
                        p.y += p.y >= 0f ? segment : -segment;
                    }
                    else
                    {
                        p.x += p.x >= 0f ? segment : -segment;
                    }
                    local.Add(p);
                }
            }
            else
            {
                return null;
            }
            Vector2[] points = local.Select(p => (Vector2)c.transform.TransformPoint(p + c.offset)).ToArray();
            if (points.Length < 3 || points.Any(p => !Finite(p.x) || !Finite(p.y)))
            {
                return null;
            }
            float area = 0f;
            for (int i = 0; i < points.Length; i++)
            {
                area += Cross(points[i], points[(i + 1) % points.Length]);
            }
            if (Mathf.Abs(area) <= 0.01f)
            {
                return null;
            }
            if (area < 0f)
            {
                Array.Reverse(points);
            }
            for (int i = 0; i < points.Length; i++)
            {
                Vector2 start = points[i];
                Vector2 edge = points[(i + 1) % points.Length] - start;
                if (edge.sqrMagnitude <= 0.000001f || points.Any(p => Cross(edge, p - start) < -0.001f))
                {
                    return null;
                }
            }
            return new Region(points);
        }

        private static bool UniformInPlane(Transform t)
        {
            Vector3 origin = t.TransformPoint(Vector3.zero);
            Vector3 x = t.TransformPoint(new Vector3(1f, 0f, 0f)) - origin;
            Vector3 y = t.TransformPoint(new Vector3(0f, 1f, 0f)) - origin;
            float x2 = x.x * x.x + x.y * x.y;
            float y2 = y.x * y.x + y.y * y.y;
            float scale2 = Mathf.Max(x2, y2);
            return Finite(x2) && Finite(y2) && x2 > 0.000001f && y2 > 0.000001f
                && x.z * x.z <= scale2 * 0.00000001f && y.z * y.z <= scale2 * 0.00000001f
                && Mathf.Abs(x2 - y2) <= scale2 * 0.0001f
                && Mathf.Abs(x.x * y.x + x.y * y.y) <= scale2 * 0.0001f;
        }

        internal bool Contains(Vector2 center, Vector2 extent)
        {
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector2 start = vertices[i];
                Vector2 edge = vertices[(i + 1) % vertices.Length] - start;
                // The minimum half-plane distance of the complete axis-aligned hero box.
                float nearest = Cross(edge, center - start) - Mathf.Abs(edge.y) * extent.x - Mathf.Abs(edge.x) * extent.y;
                if (nearest < Skin * edge.magnitude)
                {
                    return false;
                }
            }
            return true;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    }
}
