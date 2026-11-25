using System.Collections.Generic;
using UnityEngine;

namespace Godhome;

// [Godhome] 圣所里的小动效：发光呼吸、慢转、浮尘、帧动画。都只改自己物体上的渲染器，不碰游戏逻辑。

/// <summary>透明度（可选缩放）按正弦呼吸。</summary>
internal sealed class SanctumPulse : MonoBehaviour
{
    internal float Period = 4f;
    internal float MinAlpha = 0.6f;
    internal float MaxAlpha = 1f;
    internal float ScaleAmp;
    internal float Phase;
    private SpriteRenderer? sr;
    private Color baseColor;
    private Vector3 baseScale;

    private void Start()
    {
        sr = GetComponent<SpriteRenderer>();
        baseColor = sr != null ? sr.color : Color.white;
        baseScale = transform.localScale;
    }

    private void Update()
    {
        float k = 0.5f + 0.5f * Mathf.Sin((Time.time / Mathf.Max(0.1f, Period) + Phase) * Mathf.PI * 2f);
        if (sr != null)
        {
            Color c = baseColor;
            c.a = baseColor.a * Mathf.Lerp(MinAlpha, MaxAlpha, k);
            sr.color = c;
        }
        if (ScaleAmp > 0f)
        {
            transform.localScale = baseScale * (1f + ScaleAmp * (k - 0.5f));
        }
    }

    /// <summary>外部改了颜色（雕像换版本）以后重新记底色。</summary>
    internal void Rebase()
    {
        if (sr != null)
        {
            baseColor = sr.color;
        }
    }
}

/// <summary>慢慢转。</summary>
internal sealed class SanctumSpin : MonoBehaviour
{
    internal float DegreesPerSecond = 6f;

    private void Update() => transform.Rotate(0f, 0f, DegreesPerSecond * Time.deltaTime);
}

/// <summary>逐帧换精灵（传送门的丝光）。</summary>
internal sealed class SanctumFrames : MonoBehaviour
{
    internal Sprite[] Frames = System.Array.Empty<Sprite>();
    internal float Fps = 10f;
    private SpriteRenderer? sr;

    private void Start() => sr = GetComponent<SpriteRenderer>();

    private void Update()
    {
        if (sr == null || Frames.Length == 0)
        {
            return;
        }
        int i = (int)(Time.time * Fps) % Frames.Length;
        if (Frames[i] != null)
        {
            sr.sprite = Frames[i];
        }
    }
}

/// <summary>
/// 一片区域里缓缓上浮的光点（丝屑、金尘）。子物体是普通的精灵渲染器，数量固定，飘出顶部就回到底部。
/// </summary>
internal sealed class SanctumMotes : MonoBehaviour
{
    internal Rect Area;
    internal float Z;
    internal Sprite? Sprite;
    internal Color Tint = new(1f, 0.85f, 0.55f, 0.6f);
    internal int Count = 30;
    internal float MinSize = 0.06f;
    internal float MaxSize = 0.16f;
    internal float Speed = 0.35f;
    internal Material? Material;
    internal int SortingLayer;
    internal int SortingOrder;

    private readonly List<(Transform T, SpriteRenderer R, float Speed, float Sway, float Phase, float Alpha)> motes = new();

    private void Start()
    {
        if (Sprite == null)
        {
            return;
        }
        var rng = new System.Random(GetInstanceID());
        for (int i = 0; i < Count; i++)
        {
            var go = new GameObject("Mote");
            go.transform.SetParent(transform, false);
            float s = Mathf.Lerp(MinSize, MaxSize, (float)rng.NextDouble());
            go.transform.localScale = new Vector3(s, s, 1f);
            go.transform.position = new Vector3(Area.xMin + (float)rng.NextDouble() * Area.width, Area.yMin + (float)rng.NextDouble() * Area.height, Z + (float)rng.NextDouble() * 2f - 1f);
            SpriteRenderer r = go.AddComponent<SpriteRenderer>();
            r.sprite = Sprite;
            if (Material != null)
            {
                r.sharedMaterial = Material;
            }
            r.sortingLayerID = SortingLayer;
            r.sortingOrder = SortingOrder;
            r.color = Tint;
            motes.Add((go.transform, r, Speed * Mathf.Lerp(0.5f, 1.4f, (float)rng.NextDouble()), Mathf.Lerp(0.2f, 0.7f, (float)rng.NextDouble()), (float)rng.NextDouble() * 10f, Mathf.Lerp(0.4f, 1f, (float)rng.NextDouble())));
        }
    }

    private void Update()
    {
        float t = Time.time;
        float dt = Time.deltaTime;
        foreach ((Transform tr, SpriteRenderer r, float speed, float sway, float phase, float alpha) in motes)
        {
            Vector3 p = tr.position;
            p.y += speed * dt;
            p.x += Mathf.Sin(t * 0.7f + phase) * sway * dt;
            if (p.y > Area.yMax)
            {
                p.y = Area.yMin;
            }
            tr.position = p;
            // 底部和顶部淡入淡出，中间闪烁。
            float h = Mathf.InverseLerp(Area.yMin, Area.yMax, p.y);
            float edge = Mathf.Clamp01(Mathf.Min(h, 1f - h) * 5f);
            Color c = Tint;
            c.a = Tint.a * alpha * edge * (0.65f + 0.35f * Mathf.Sin(t * 2.1f + phase * 3f));
            r.color = c;
        }
    }
}
