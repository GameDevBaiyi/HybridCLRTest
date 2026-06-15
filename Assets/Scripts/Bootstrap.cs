using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using HybridCLR;
using Sirenix.OdinInspector;
using UnityEngine;
using UnityEngine.Networking;

namespace HybridCLRTest
{
    // 热更启动器（AOT 侧，发布后不可改）。
    // 职责：补充 AOT 元数据 -> 加载热更程序集 -> 反射进入热更入口。
    // 编辑器下热更程序集已编译进当前域，直接按名取用。
    // IL2CPP 包内的 dll 读取优先级：CDN 下载 -> persistentDataPath 缓存 -> StreamingAssets 基线。
    // 这里用 GitHub raw 直链当伪 CDN：更新只需 git push 新 dll，设备不用 adb、不用重打包。
    public class Bootstrap : MonoBehaviour
    {
        // Generate/All 之后，从 HybridCLRGenerate/AOTGenericReferences.cs 的
        // PatchedAOTAssemblyList 把名字抄到这里；hello world 无泛型实例时留空即可。
        private static readonly List<string> AOTMetaAssemblyFiles = new List<string>();

        [Tooltip("热更资源根 URL，结尾要带 /。GitHub raw 形如 https://raw.githubusercontent.com/<user>/<repo>/<branch>/<dir>/")]
        [LabelText("CDN 根地址")]
        [SerializeField] private string _cdnBaseUrl = "https://raw.githubusercontent.com/GameDevBaiyi/HybridCLRTest/main/CDN/Android/";

        [LabelText("热更程序集名")]
        [SerializeField] private string _hotUpdateAssemblyName = "HotUpdate";

        [LabelText("入口类型全名")]
        [SerializeField] private string _entryTypeFullName = "HybridCLRTest.HotUpdate.Hello";

        [LabelText("入口方法名")]
        [SerializeField] private string _entryMethodName = "Run";

        private async UniTaskVoid Start()
        {
            await LoadAOTMetadataAsync();

            Assembly hotUpdateAssembly = await LoadHotUpdateAssemblyAsync();
            if (hotUpdateAssembly == null)
            {
                Debug.LogError($"[Bootstrap] 热更程序集 {_hotUpdateAssemblyName} 加载失败");
                return;
            }

            Type entryType = hotUpdateAssembly.GetType(_entryTypeFullName);
            MethodInfo entry = entryType?.GetMethod(_entryMethodName, BindingFlags.Public | BindingFlags.Static);
            if (entry == null)
            {
                Debug.LogError($"[Bootstrap] 找不到入口 {_entryTypeFullName}.{_entryMethodName}");
                return;
            }

            entry.Invoke(null, null);
        }

        private async UniTask<Assembly> LoadHotUpdateAssemblyAsync()
        {
#if UNITY_EDITOR
            await UniTask.CompletedTask;
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name == _hotUpdateAssemblyName);
#else
            byte[] dllBytes = await ReadDllBytesAsync(_hotUpdateAssemblyName + ".dll.bytes");
            if (dllBytes == null)
            {
                return null;
            }
            return Assembly.Load(dllBytes);
#endif
        }

        private async UniTask LoadAOTMetadataAsync()
        {
            foreach (string aotDllName in AOTMetaAssemblyFiles)
            {
                byte[] dllBytes = await ReadDllBytesAsync(aotDllName + ".bytes");
                if (dllBytes == null)
                {
                    continue;
                }
                LoadImageErrorCode err = RuntimeApi.LoadMetadataForAOTAssembly(dllBytes, HomologousImageMode.SuperSet);
                Debug.Log($"[Bootstrap] LoadMetadataForAOTAssembly {aotDllName}. ret={err}");
            }
        }

        // 加载优先级：CDN 下载（命中即写入 persistent 缓存）-> persistent 缓存 -> StreamingAssets 基线。
        private async UniTask<byte[]> ReadDllBytesAsync(string fileName)
        {
            byte[] cdnBytes = await DownloadFromCdnAsync(fileName);
            if (cdnBytes != null)
            {
                await CachePersistentAsync(fileName, cdnBytes);
                return cdnBytes;
            }

            string persistentPath = Path.Combine(Application.persistentDataPath, fileName);
            if (File.Exists(persistentPath))
            {
                Debug.Log($"[Bootstrap] CDN 不可达，用 persistent 缓存 {fileName}");
                return await File.ReadAllBytesAsync(persistentPath);
            }

            Debug.Log($"[Bootstrap] CDN/缓存均无，回退 StreamingAssets 基线 {fileName}");
            return await ReadStreamingAssetBytesAsync(fileName);
        }

        private async UniTask<byte[]> DownloadFromCdnAsync(string fileName)
        {
            if (string.IsNullOrEmpty(_cdnBaseUrl))
            {
                return null;
            }

            // 加时间戳查询参数绕过 CDN 缓存，测试期能立刻拿到刚 push 的新 dll。
            string url = _cdnBaseUrl + fileName + "?ts=" + DateTime.UtcNow.Ticks;
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.SetRequestHeader("Cache-Control", "no-cache");
                await request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[Bootstrap] CDN 下载失败 {fileName}: {request.error}");
                    return null;
                }
                Debug.Log($"[Bootstrap] CDN 命中 {fileName}（{request.downloadHandler.data.Length} bytes）");
                return request.downloadHandler.data;
            }
        }

        private static async UniTask CachePersistentAsync(string fileName, byte[] bytes)
        {
            string persistentPath = Path.Combine(Application.persistentDataPath, fileName);
            await File.WriteAllBytesAsync(persistentPath, bytes);
        }

        private static async UniTask<byte[]> ReadStreamingAssetBytesAsync(string fileName)
        {
            string path = Path.Combine(Application.streamingAssetsPath, fileName);
            // Android 上 StreamingAssets 已是 jar:file:// 形式（含 "://"），其余平台补上 file:// 前缀。
            if (!path.Contains("://"))
            {
                path = "file://" + path;
            }

            using (UnityWebRequest request = UnityWebRequest.Get(path))
            {
                await request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[Bootstrap] 读取 StreamingAssets 失败 {fileName}: {request.error}");
                    return null;
                }
                return request.downloadHandler.data;
            }
        }
    }
}
