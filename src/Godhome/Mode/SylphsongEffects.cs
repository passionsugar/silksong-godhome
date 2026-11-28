using System;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Godhome;

// Presentation for the HK1 seat. The fallback Silksong bench runs its own Eva Heal FSM.
internal sealed class SylphsongEffects : MonoBehaviour
{
    private AsyncOperationHandle<GameObject> glowAsset;
    private AsyncOperationHandle<AudioClip> soundAsset;
    private PlayMakerFSM? glow;

    private void Start()
    {
        glowAsset = Borrow<GameObject>("localpoolprefabs_assets_bench", "Assets/Sprites/Effects/Bench_Silk_Recharge/Silk_Recharge_glow.prefab");
        soundAsset = Borrow<AudioClip>("herosfxstatic_assets_all", "Assets/Audio/SFX/Heroes/Knight/focus_ready.wav");
    }

    private static AsyncOperationHandle<T> Borrow<T>(string group, string path) where T : UnityEngine.Object
    {
        try
        {
            var bundle = GameAssetBundle.Find(group);
            var provider = Addressables.ResourceManager.ResourceProviders.OfType<BundledAssetProvider>().FirstOrDefault();
            if (bundle != null && provider != null)
            {
                var asset = new ResourceLocationBase(path, path, provider.ProviderId, typeof(T), bundle);
                return Addressables.ResourceManager.ProvideResource<T>(asset);
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] Sylphsong asset {path}: {e.Message}");
        }
        return default;
    }

    internal void Pulse(Transform hero)
    {
        if (glow == null && glowAsset.IsValid() && glowAsset.IsDone && glowAsset.Status == AsyncOperationStatus.Succeeded)
        {
            var effect = Instantiate(glowAsset.Result, hero.position + Vector3.down * 0.367685f, Quaternion.identity, transform);
            glow = effect.GetComponent<PlayMakerFSM>();
        }
        if (soundAsset.IsValid() && soundAsset.IsDone && soundAsset.Status == AsyncOperationStatus.Succeeded)
        {
            new AudioEvent { Clip = soundAsset.Result, PitchMin = 0.9f, PitchMax = 1.1f, Volume = 1f }.SpawnAndPlayOneShot(transform.position);
        }
    }

    internal void End(bool full)
    {
        if (glow != null)
        {
            glow.SendEvent(full ? "MAX" : "STOP");
            glow = null;
        }
    }

    private void OnDestroy()
    {
        if (glowAsset.IsValid()) Addressables.Release(glowAsset);
        if (soundAsset.IsValid()) Addressables.Release(soundAsset);
    }
}
