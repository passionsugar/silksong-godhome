using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 调试：录宣传片（Debug.Promo，默认关）。
/// - 画面：所有直接画到屏幕的镜头改画进一张 1920×1080 的图（远程桌面的屏幕是竖的、很小），每帧异步读回，
///   原始像素经管道交给 ffmpeg（Debug.PromoFfmpeg）编码成 .mkv。
/// - 时间：录的时候 Time.captureDeltaTime = 1/60，游戏每帧固定走 1/60 秒，不管这一帧实际用了多久，所以画面不掉帧。
/// - 声音：AudioRenderer 按同样的游戏时间把混音输出录成 .wav（录的时候扬声器没声音），和画面一帧一帧对齐。
/// - 宽高比：ForceCameraAspect 按屏幕算视口，录的时候让它按 16:9 算（视口铺满、镜头视野和正常 16:9 屏幕一样）。
/// - 记号：场景、模式状态、导演的每一步写进 .marks.tsv（帧号、游戏时间），剪辑时按它找片段。
/// 只在宣传片导演（DebugTools.Promo）里用，普通游戏里不会创建。
/// </summary>
internal sealed class PromoRecorder : MonoBehaviour
{
    internal const int W = 1920;
    internal const int H = 1080;
    internal const int Fps = 60;

    internal static PromoRecorder? Instance { get; private set; }

    internal static bool Recording => Instance != null && Instance.recording;

    /// <summary>录的时候 ForceCameraAspect 按 16:9 算视口（PromoAspectPatch）。</summary>
    internal static bool FakeScreen { get; private set; }

    internal static int Frame => Instance != null ? Instance.frame : 0;

    internal string BaseName => baseName;

    private bool recording;
    private int frame;
    private int readbacks;
    private int readErrors;
    private long written;
    private RenderTexture? rt;
    private Process? ff;
    private Stream? ffIn;
    private Thread? writer;
    private BlockingCollection<byte[]>? queue;
    private readonly ConcurrentBag<byte[]> pool = new();
    private byte[]? lastFrame;
    private volatile string writerError = "";
    private FileStream? wav;
    private long wavFrames;
    private int channels = 2;
    private int rate = 48000;
    private bool audioOn;
    private StreamWriter? marks;
    private string baseName = "";
    private float oldCaptureDelta;
    private int oldVsync;
    private int oldTarget;
    private readonly List<(AudioMixer Mixer, float Db)> muted = new();
    private Material? coverMat;
    private string lastScene = "";
    private string lastState = "";
    private string lastBoss = "";
    private readonly StringBuilder ffLog = new();
    private float startReal;

    internal static PromoRecorder Ensure(GameObject host)
    {
        if (Instance == null)
        {
            Instance = host.GetComponent<PromoRecorder>() ?? host.AddComponent<PromoRecorder>();
        }
        return Instance;
    }

    private static int SpeakerChannels(AudioSpeakerMode m) => m switch
    {
        AudioSpeakerMode.Mono => 1,
        AudioSpeakerMode.Stereo => 2,
        AudioSpeakerMode.Quad => 4,
        AudioSpeakerMode.Surround => 5,
        AudioSpeakerMode.Mode5point1 => 6,
        AudioSpeakerMode.Mode7point1 => 8,
        _ => 2,
    };

    /// <summary>开始录。name 只用在文件名里。失败返回 false（什么都没改）。</summary>
    internal bool Begin(string name, out string error)
    {
        error = "";
        if (recording)
        {
            error = "已经在录 / already recording";
            return false;
        }
        string ffmpeg = GodhomeConfig.DebugPromoFfmpeg.Value?.Trim().Trim('"') ?? "";
        if (ffmpeg.Length == 0 || !File.Exists(ffmpeg))
        {
            error = $"找不到 ffmpeg / ffmpeg not found: \"{ffmpeg}\"";
            return false;
        }
        string dir = GodhomeConfig.DebugPromoOut.Value?.Trim().Trim('"') ?? "";
        if (dir.Length == 0)
        {
            dir = Path.Combine(Paths.BepInExRootPath, "Godhome", "promo");
        }
        try
        {
            Directory.CreateDirectory(dir);
            baseName = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}-{name}");
            rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { name = "Godhome Promo RT", antiAliasing = 1 };
            rt.Create();
            var psi = new ProcessStartInfo(ffmpeg,
                $"-hide_banner -loglevel warning -y -f rawvideo -pix_fmt rgba -s {W}x{H} -framerate {Fps} -i - -vf vflip " +
                $"-c:v libx264 -preset veryfast -crf 14 -pix_fmt yuv420p -g 120 \"{baseName}.mkv\"")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            ff = Process.Start(psi);
            if (ff == null)
            {
                throw new InvalidOperationException("Process.Start returned null");
            }
            ff.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    lock (ffLog)
                    {
                        if (ffLog.Length < 20000)
                        {
                            ffLog.AppendLine(e.Data);
                        }
                    }
                }
            };
            ff.BeginErrorReadLine();
            ff.OutputDataReceived += (_, _) => { };
            ff.BeginOutputReadLine();
            ffIn = ff.StandardInput.BaseStream;
            queue = new BlockingCollection<byte[]>(6);
            writer = new Thread(WriteLoop) { IsBackground = true, Name = "Godhome promo writer" };
            writer.Start();
            marks = new StreamWriter(baseName + ".marks.tsv", false, new UTF8Encoding(false)) { AutoFlush = true };
            marks.WriteLine("frame\tgameTime\treal\tevent\tdetail");
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            Cleanup();
            return false;
        }
        rate = AudioSettings.outputSampleRate;
        channels = SpeakerChannels(AudioSettings.speakerMode);
        oldCaptureDelta = Time.captureDeltaTime;
        oldVsync = QualitySettings.vSyncCount;
        oldTarget = Application.targetFrameRate;
        Time.captureDeltaTime = 1f / Fps;
        QualitySettings.vSyncCount = 0;
        // 限到 60 帧：游戏时间和真实时间差不多一样快（自检的等待按真实时间算；声音退回监听器录制时也不会跑偏）。
        Application.targetFrameRate = Fps;
        try
        {
            wav = new FileStream(baseName + ".wav", FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
            WriteWavHeader(wav, 0);
            audioOn = AudioRenderer.Start();
            audioMode = audioOn ? 1 : 0;
            if (!audioOn)
            {
                StartTap();
            }
            sync = new StreamWriter(baseName + ".sync.tsv", false, new UTF8Encoding(false));
            sync.WriteLine("frame\taudioFrames\treal");
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 宣传片：录音开不了 / promo: audio capture failed: {e.Message}");
            audioOn = false;
        }
        FakeScreen = true;
        RefreshAspect();
        if (GodhomeConfig.DebugPromoMuteMusic.Value)
        {
            MuteMusic();
        }
        frame = 0;
        readbacks = 0;
        readErrors = 0;
        written = 0;
        wavFrames = 0;
        startReal = Time.realtimeSinceStartup;
        recording = true;
        Mark("begin", $"{name} {W}x{H}@{Fps} audio={rate}Hz x{channels} mode={audioMode} asyncReadback={SystemInfo.supportsAsyncGPUReadback}");
        GodhomePlugin.Log.LogInfo($"[Godhome] 宣传片开始录 / promo recording started: {baseName}.mkv（声音 / audio {(audioOn ? $"{rate} Hz × {channels}" : "没有 / off")}）");
        StartCoroutine(EndOfFrames());
        return true;
    }

    /// <summary>在记号文件里记一行（帧号 = 下一帧要写的那一帧）。</summary>
    internal static void Mark(string ev, string detail = "")
    {
        PromoRecorder? r = Instance;
        if (r == null || r.marks == null)
        {
            return;
        }
        try
        {
            r.marks.WriteLine($"{r.frame}\t{Time.time:0.000}\t{Time.realtimeSinceStartup - r.startReal:0.0}\t{ev}\t{detail.Replace('\t', ' ').Replace('\n', ' ')}");
        }
        catch (Exception)
        {
            // 写不进去就算了。
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 宣传片记号 / promo mark @{r.frame}: {ev} {detail}");
    }

    private void LateUpdate()
    {
        if (!recording)
        {
            return;
        }
        Redirect();
        if (muted.Count > 0)
        {
            foreach ((AudioMixer m, _) in muted)
            {
                if (m != null)
                {
                    m.SetFloat("MusicVolume", -80f);
                }
            }
        }
        // 场景、模式状态、当前 Boss 一变就记一行。
        string scene = GameManager.UnsafeInstance != null ? GameManager.UnsafeInstance.GetSceneNameString() ?? "" : "";
        if (scene != lastScene)
        {
            lastScene = scene;
            Mark("scene", scene);
        }
        ModeController? mc = ModeController.Instance;
        string st = mc != null ? mc.State.ToString() : "-";
        string boss = mc != null && mc.CurrentBoss != null ? mc.CurrentBoss.Id : "";
        if (st != lastState || boss != lastBoss)
        {
            lastState = st;
            lastBoss = boss;
            Mark("state", $"{st} {boss}");
        }
        if (writerError.Length > 0)
        {
            string err = writerError;
            writerError = "";
            GodhomePlugin.Log.LogError($"[Godhome] 宣传片写 ffmpeg 出错，停止录像 / promo: writing to ffmpeg failed, stopping: {err}");
            StartCoroutine(End());
        }
    }

    /// <summary>直接画到屏幕上的镜头（视口不是零大小的）都改画进录像的图。每帧都查一遍（换场景会有新镜头）。</summary>
    private void Redirect()
    {
        if (rt == null)
        {
            return;
        }
        foreach (Camera c in Camera.allCameras)
        {
            if (c == null || !c.enabled || c.targetTexture != null || c.rect.width < 0.001f || c.rect.height < 0.001f)
            {
                continue;
            }
            c.targetTexture = rt;
        }
    }

    private void RestoreCameras()
    {
        if (rt == null)
        {
            return;
        }
        foreach (Camera c in Resources.FindObjectsOfTypeAll<Camera>())
        {
            if (c != null && c.targetTexture == rt)
            {
                c.targetTexture = null;
            }
        }
    }

    private IEnumerator EndOfFrames()
    {
        var eof = new WaitForEndOfFrame();
        while (recording)
        {
            yield return eof;
            if (!recording || rt == null)
            {
                break;
            }
            try
            {
                DrawCover();
                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    readbacks++;
                    AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, OnReadback);
                }
                else
                {
                    ReadSync();
                }
                CaptureAudio();
                // 屏幕上也显示一份（远程桌面里看得到在录什么）。
                Graphics.Blit(rt, (RenderTexture?)null);
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 宣传片这一帧出错 / promo frame {frame} failed: {e.Message}");
            }
            frame++;
        }
    }

    /// <summary>织忆圣所的黑幕是 OnGUI 画到屏幕上的，不在镜头里：录的时候照它的透明度在录像的图上也盖一层。</summary>
    private void DrawCover()
    {
        float a = LoomUI.Instance != null ? LoomUI.Instance.CoverAlpha : 0f;
        if (a <= 0.001f || rt == null)
        {
            return;
        }
        if (coverMat == null)
        {
            Shader? sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null)
            {
                return;
            }
            coverMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            coverMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            coverMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            coverMat.SetInt("_Cull", (int)CullMode.Off);
            coverMat.SetInt("_ZWrite", 0);
            coverMat.SetInt("_ZTest", (int)CompareFunction.Always);
        }
        RenderTexture? prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.PushMatrix();
        GL.LoadOrtho();
        coverMat.SetPass(0);
        GL.Begin(GL.QUADS);
        GL.Color(new Color(0f, 0f, 0f, Mathf.Clamp01(a)));
        GL.Vertex3(0f, 0f, 0f);
        GL.Vertex3(1f, 0f, 0f);
        GL.Vertex3(1f, 1f, 0f);
        GL.Vertex3(0f, 1f, 0f);
        GL.End();
        GL.PopMatrix();
        RenderTexture.active = prev;
    }

    private void OnReadback(AsyncGPUReadbackRequest req)
    {
        if (queue == null || queue.IsAddingCompleted)
        {
            return;
        }
        byte[] buf = pool.TryTake(out byte[]? b) ? b : new byte[W * H * 4];
        if (req.hasError)
        {
            // 读不回来：用上一帧顶上，帧数不少（声音才对得上）。
            readErrors++;
            if (lastFrame != null)
            {
                Buffer.BlockCopy(lastFrame, 0, buf, 0, buf.Length);
            }
        }
        else
        {
            NativeArray<byte> data = req.GetData<byte>();
            if (data.Length == buf.Length)
            {
                data.CopyTo(buf);
            }
            else
            {
                readErrors++;
            }
        }
        lastFrame ??= new byte[W * H * 4];
        Buffer.BlockCopy(buf, 0, lastFrame, 0, buf.Length);
        try
        {
            queue.Add(buf);
        }
        catch (InvalidOperationException)
        {
            // 已经停了。
        }
    }

    private Texture2D? syncTex;

    private void ReadSync()
    {
        if (rt == null || queue == null)
        {
            return;
        }
        syncTex ??= new Texture2D(W, H, TextureFormat.RGBA32, false);
        RenderTexture? prev = RenderTexture.active;
        RenderTexture.active = rt;
        syncTex.ReadPixels(new Rect(0, 0, W, H), 0, 0, false);
        RenderTexture.active = prev;
        byte[] buf = pool.TryTake(out byte[]? b) ? b : new byte[W * H * 4];
        syncTex.GetRawTextureData<byte>().CopyTo(buf);
        queue.Add(buf);
    }

    // 声音：1 = AudioRenderer（按游戏时间渲染，和画面逐帧对齐）；2 = 监听器上的 OnAudioFilterRead（真实时间，靠 .sync.tsv 对齐）。
    private int audioMode;
    private double audioDebt;
    private int rendererTries;
    private long rendererGot;
    private StreamWriter? sync;
    private readonly ConcurrentQueue<float[]> tapQ = new();
    private long tapFrames;
    private int tapChannels;

    private sealed class ListenerTap : MonoBehaviour
    {
        internal PromoRecorder? Owner;
        private AudioListener? listener;

        private void Awake() => listener = GetComponent<AudioListener>();

        private void OnAudioFilterRead(float[] data, int ch)
        {
            PromoRecorder? o = Owner;
            if (o == null || listener == null || o.audioMode != 2 || !o.recording)
            {
                return;
            }
            var copy = new float[data.Length];
            Array.Copy(data, copy, data.Length);
            o.tapChannels = ch;
            o.tapQ.Enqueue(copy);
            Interlocked.Add(ref o.tapFrames, data.Length / Math.Max(1, ch));
        }
    }

    private void StartTap()
    {
        audioMode = 2;
        AttachTap();
        Mark("audio-tap", "OnAudioFilterRead on the AudioListener");
    }

    private void AttachTap()
    {
        foreach (AudioListener l in Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
        {
            if (l != null && l.enabled && l.GetComponent<ListenerTap>() == null)
            {
                ListenerTap t = l.gameObject.AddComponent<ListenerTap>();
                t.Owner = this;
                GodhomePlugin.Log.LogInfo($"[Godhome] 宣传片：监听器录音 / promo: tapping AudioListener on {SceneTools.PathOf(l.transform)}");
            }
        }
    }

    private void CaptureAudio()
    {
        if (wav == null)
        {
            return;
        }
        if (audioMode == 1)
        {
            int n = AudioRenderer.GetSampleCountForCaptureFrame();
            if (n <= 0)
            {
                // 有的版本这里总是 0：自己按采样率 / 帧率算。
                audioDebt += rate / (double)Fps;
                n = (int)Math.Floor(audioDebt);
                audioDebt -= n;
            }
            bool ok = false;
            float peak = 0f;
            if (n > 0)
            {
                var buf = new NativeArray<float>(n * channels, Allocator.Temp);
                try
                {
                    ok = AudioRenderer.Render(buf);
                    if (ok)
                    {
                        for (int i = 0; i < buf.Length; i += 7)
                        {
                            peak = Mathf.Max(peak, Mathf.Abs(buf[i]));
                        }
                        byte[] arr = buf.Reinterpret<byte>(4).ToArray();
                        wav.Write(arr, 0, arr.Length);
                        wavFrames += n;
                        if (peak > 1e-5f)
                        {
                            rendererGot++;
                        }
                    }
                }
                finally
                {
                    buf.Dispose();
                }
            }
            rendererTries++;
            if (rendererTries <= 3 || rendererTries == 120)
            {
                Mark("audio-renderer", $"n={n} ok={ok} peak={peak:0.0000} count={AudioRenderer.GetSampleCountForCaptureFrame()} nonSilent={rendererGot}/{rendererTries}");
            }
            // 两秒都录不到声音：换成监听器录音（前面录到的静音留着，时间对得上）。
            if (rendererTries >= 120 && rendererGot == 0)
            {
                try
                {
                    AudioRenderer.Stop();
                }
                catch (Exception)
                {
                }
                audioOn = false;
                StartTap();
            }
        }
        else if (audioMode == 2)
        {
            if (Time.frameCount % 30 == 0)
            {
                AttachTap();
            }
            while (tapQ.TryDequeue(out float[]? d))
            {
                if (tapChannels != channels && wavFrames == 0)
                {
                    channels = tapChannels;
                }
                byte[] arr = new byte[d.Length * 4];
                Buffer.BlockCopy(d, 0, arr, 0, arr.Length);
                wav.Write(arr, 0, arr.Length);
                wavFrames += d.Length / Math.Max(1, channels);
            }
        }
        try
        {
            sync?.WriteLine($"{frame}\t{wavFrames}\t{Time.realtimeSinceStartup - startReal:0.000}");
        }
        catch (Exception)
        {
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (byte[] b in queue!.GetConsumingEnumerable())
            {
                ffIn!.Write(b, 0, b.Length);
                written++;
                pool.Add(b);
            }
        }
        catch (Exception e)
        {
            writerError = $"{e.GetType().Name}: {e.Message}";
        }
    }

    private void WriteWavHeader(Stream s, long dataBytes)
    {
        using var w = new BinaryWriter(s, Encoding.ASCII, leaveOpen: true);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write((uint)Math.Min(uint.MaxValue, 36 + dataBytes));
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)3);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * 4);
        w.Write((short)(channels * 4));
        w.Write((short)32);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write((uint)Math.Min(uint.MaxValue, dataBytes));
    }

    private void MuteMusic()
    {
        muted.Clear();
        foreach (AudioMixer m in Resources.FindObjectsOfTypeAll<AudioMixer>())
        {
            if (m != null && m.GetFloat("MusicVolume", out float db))
            {
                muted.Add((m, db));
                m.SetFloat("MusicVolume", -80f);
            }
        }
        Mark("music-muted", $"{muted.Count} mixer(s)");
    }

    private void UnmuteMusic()
    {
        foreach ((AudioMixer m, float db) in muted)
        {
            if (m != null)
            {
                m.SetFloat("MusicVolume", db);
            }
        }
        muted.Clear();
    }

    /// <summary>让 ForceCameraAspect 马上按现在的设定重算视口（它只在屏幕大小变了时重算）。</summary>
    private static void RefreshAspect()
    {
        foreach (ForceCameraAspect f in Object.FindObjectsByType<ForceCameraAspect>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            try
            {
                Traverse.Create(f).Field("lastX").SetValue(-1);
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 宣传片：重算视口失败 / promo: aspect refresh failed: {e.Message}");
            }
        }
    }

    /// <summary>停止录像（协程：等最后几帧读回来，再关 ffmpeg）。</summary>
    internal IEnumerator End()
    {
        if (!recording)
        {
            yield break;
        }
        Mark("end", $"frames {frame}");
        recording = false;
        RestoreTime();
        AsyncGPUReadback.WaitAllRequests();
        yield return null;
        yield return null;
        yield return null;
        Finish();
    }

    private void RestoreTime()
    {
        audioMode = 0;
        if (audioOn)
        {
            try
            {
                AudioRenderer.Stop();
            }
            catch (Exception)
            {
            }
            audioOn = false;
        }
        Time.captureDeltaTime = oldCaptureDelta;
        QualitySettings.vSyncCount = oldVsync;
        Application.targetFrameRate = oldTarget;
        RestoreCameras();
        FakeScreen = false;
        RefreshAspect();
        UnmuteMusic();
    }

    private void Finish()
    {
        try
        {
            queue?.CompleteAdding();
            writer?.Join(60000);
            ffIn?.Flush();
            ffIn?.Close();
            ff?.WaitForExit(120000);
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 宣传片关 ffmpeg 出错 / promo: closing ffmpeg failed: {e.Message}");
        }
        long dataBytes = wavFrames * channels * 4;
        try
        {
            if (wav != null)
            {
                wav.Flush();
                wav.Seek(0, SeekOrigin.Begin);
                WriteWavHeader(wav, dataBytes);
                wav.Close();
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 宣传片写 wav 头出错 / promo: wav header failed: {e.Message}");
        }
        string fflog;
        lock (ffLog)
        {
            fflog = ffLog.ToString().Trim();
        }
        int exit = -999;
        try
        {
            exit = ff != null && ff.HasExited ? ff.ExitCode : -1;
        }
        catch (Exception)
        {
        }
        string summary = $"帧 / frames {frame}，读回 / readbacks {readbacks}，读回出错 / read errors {readErrors}，写进 ffmpeg / written {written}，声音 / audio frames {wavFrames}（{(rate > 0 ? wavFrames / (double)rate : 0):0.00}s），ffmpeg exit {exit}";
        try
        {
            marks?.WriteLine($"{frame}\t{Time.time:0.000}\t{Time.realtimeSinceStartup - startReal:0.0}\tsummary\t{summary}");
            if (fflog.Length > 0)
            {
                marks?.WriteLine($"{frame}\t{Time.time:0.000}\t0\tffmpeg-log\t{fflog.Replace('\n', ' ').Replace('\r', ' ')}");
            }
            marks?.Close();
            sync?.Close();
        }
        catch (Exception)
        {
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 宣传片录完 / promo recording finished: {baseName}.mkv；{summary}{(fflog.Length > 0 ? "；ffmpeg: " + fflog : "")}");
        Cleanup();
    }

    private void Cleanup()
    {
        marks = null;
        sync = null;
        while (tapQ.TryDequeue(out _))
        {
        }
        foreach (ListenerTap t in Object.FindObjectsByType<ListenerTap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (t != null)
            {
                Object.Destroy(t);
            }
        }
        wav = null;
        queue = null;
        writer = null;
        ffIn = null;
        ff = null;
        lastFrame = null;
        while (pool.TryTake(out _))
        {
        }
        if (rt != null)
        {
            RestoreCameras();
            rt.Release();
            Object.Destroy(rt);
            rt = null;
        }
    }

    private void OnApplicationQuit()
    {
        if (recording)
        {
            Mark("quit");
            recording = false;
            RestoreTime();
            try
            {
                AsyncGPUReadback.WaitAllRequests();
            }
            catch (Exception)
            {
            }
            Finish();
        }
    }

    private void OnDestroy()
    {
        if (recording)
        {
            recording = false;
            RestoreTime();
            Finish();
        }
        if (Instance == this)
        {
            Instance = null;
        }
    }
}

/// <summary>录宣传片时 ForceCameraAspect 按 16:9 算视口：视口铺满、镜头视野和 16:9 屏幕一样（远程桌面的屏幕是竖的）。</summary>
[HarmonyPatch(typeof(ForceCameraAspect), nameof(ForceCameraAspect.AutoScaleViewportShared))]
internal static class PromoAspectPatch
{
    private static bool Prefix(float scaleAdjust, ref Rect currentViewportRect, ref Rect newViewPortRect, ref float heightMult, ref float __result)
    {
        if (!PromoRecorder.FakeScreen)
        {
            return true;
        }
        float s = 1f + scaleAdjust;
        newViewPortRect = currentViewportRect;
        newViewPortRect.width = s;
        newViewPortRect.height = s;
        newViewPortRect.x = (1f - s) / 2f;
        newViewPortRect.y = (1f - s) / 2f;
        heightMult = 1f;
        __result = 16f / 9f;
        return false;
    }
}
