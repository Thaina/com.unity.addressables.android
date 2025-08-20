#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
#define RUNTIME_MOBILE
#endif

using System.ComponentModel;

using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.ResourceProviders;

#if RUNTIME_MOBILE
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

using UnityEngine.Android;
using UnityEngine.ResourceManagement.Util;
using UnityEngine.ResourceManagement.Exceptions;
using UnityEngine.ResourceManagement.ResourceLocations;
#endif

#if UNITY_IOS
using UnityEngine.iOS;
#endif

namespace UnityEngine.AddressableAssets.Android
{
#if RUNTIME_MOBILE
    // this class is required to generate error when trying loading synchronously
    class PlayAssetDeliveryResource
    {
        PlayAssetDeliveryAssetBundleProvider m_PlayAssetDeliveryAssetBundleProvider;
        ProvideHandle m_ProvideHandle;
        const string kSyncMessage = "Play Asset Delivery provider does not support synchronous Addressable loading. Please do not use WaitForCompletion with Play Asset Delivery provider.";

        internal PlayAssetDeliveryResource(PlayAssetDeliveryAssetBundleProvider provider, ProvideHandle provideHandle)
        {
            m_PlayAssetDeliveryAssetBundleProvider = provider;
            m_ProvideHandle = provideHandle;
            m_ProvideHandle.SetWaitForCompletionCallback(WaitForCompletionHandler);
        }

        bool WaitForCompletionHandler()
        {
            Debug.LogError(kSyncMessage);
            m_PlayAssetDeliveryAssetBundleProvider.CompleteInterface(m_ProvideHandle);
            m_ProvideHandle.Complete<AssetBundleResource>(null, false, new RemoteProviderException(kSyncMessage));
            return true;
        }
    }
#endif

    /// <summary>
    /// Ensures that the asset pack containing the AssetBundle is installed/downloaded before attemping to load the bundle.
    /// </summary>
    [DisplayName("Play Asset Delivery Provider")]
    public class PlayAssetDeliveryAssetBundleProvider : AssetBundleProvider, IUpdateReceiver
    {
#if RUNTIME_MOBILE
        Dictionary<string,HashSet<ProvideHandle>> m_ProviderInterfaces = new Dictionary<string,HashSet<ProvideHandle>>();
        List<string> m_AssetPackQueue = new List<string>();

        /// <inheritdoc/>
        public override void Provide(ProvideHandle providerInterface)
        {
            LoadFromAssetPack(providerInterface);
        }

        void LoadFromAssetPack(ProvideHandle providerInterface)
        {
            if(!PlayAssetDeliveryRuntimeData.Instance.Initialized)
            {
                // this can happen only when trying to load first asset using Play Asset Delivery synchronously
                new PlayAssetDeliveryResource(this,providerInterface);
                return;
            }

            string bundleName = Path.GetFileNameWithoutExtension(providerInterface.Location.InternalId.Replace("\\","/"));
            if(!PlayAssetDeliveryRuntimeData.Instance.BundleNameToAssetPack.TryGetValue(bundleName,out var assetPack))
            {
                // Bundle is either assigned to the generated asset packs, or not assigned to any asset pack
                base.Provide(providerInterface);
                return;
            }

            var assetPackNameToDownloadPath = PlayAssetDeliveryRuntimeData.Instance.AssetPackNameToDownloadPath;
            var assetPackName = assetPack.AssetPackName;
            // Bundle is assigned to install-time AddressablesAssetPack
            if(assetPackName == CustomAssetPackUtility.kAddressablesAssetPackName)
            {
                assetPackNameToDownloadPath.Add(CustomAssetPackUtility.kAddressablesAssetPackName,Application.streamingAssetsPath);
                base.Provide(providerInterface);
                return;
            }
            // Bundle is assigned to the previously downloaded asset pack
            if(assetPackNameToDownloadPath.TryGetValue(assetPackName,out string downloadPath))
            {
                if(Directory.Exists(downloadPath))
                {
                    base.Provide(providerInterface);
                    return;
                }
                // Downloaded asset pack doesn't exist, most likely it was deleted
                assetPackNameToDownloadPath.Remove(assetPackName);
            }
            // Download the asset pack
            new PlayAssetDeliveryResource(this,providerInterface);
            DownloadRemoteAssetPack(providerInterface,assetPackName);
        }

        /// <inheritdoc/>
        public override void Release(IResourceLocation location,object asset)
        {
            base.Release(location,asset);
            m_ProviderInterfaces.Clear();
        }

        internal override IOperationCacheKey CreateCacheKeyForLocation(ResourceManager rm,IResourceLocation location,Type desiredType)
        {
            return new IdCacheKey(location.GetType(),location.InternalId);
        }

        void DownloadRemoteAssetPack(ProvideHandle providerInterface,string assetPackName)
        {
            // Note that most methods in the AndroidAssetPacks class are either direct wrappers of java APIs in Google's PlayCore plugin,
            // or depend on values that the PlayCore API returns. If the PlayCore plugin is missing, calling these methods will throw an InvalidOperationException exception.
            try
            {
                if(!m_ProviderInterfaces.TryGetValue(assetPackName,out var hashSet))
                {
                    if(m_AssetPackQueue.Count == 0)
                    {
                        Addressables.ResourceManager.AddUpdateReceiver(this);
                    }

                    hashSet = new HashSet<ProvideHandle>();
                    m_ProviderInterfaces[assetPackName] = hashSet;
                    m_AssetPackQueue.Add(assetPackName);
                }

                hashSet.Add(providerInterface);
            }
            catch(InvalidOperationException ioe)
            {
                m_ProviderInterfaces.Remove(assetPackName);
                var message = $"Cannot retrieve state for asset pack '{assetPackName}'. This might be because PlayCore Plugin is not installed: {ioe.Message}";
                Debug.LogError(message);
                providerInterface.Complete<AssetBundleResource>(null,false,new RemoteProviderException(message));
            }
        }

        void CheckDownloadStatus(AndroidAssetPackInfo info)
        {
            var message = "";
            switch(info.status)
            {
                case AndroidAssetPackStatus.Failed:
                    message = $"Failed to retrieve the state of asset pack '{info.name}'.";
                    break;
                case AndroidAssetPackStatus.Unknown:
                    message = $"Asset pack '{info.name}' is unavailable for this application. This can occur if the app was not installed through Google Play.";
                    break;
                case AndroidAssetPackStatus.Canceled:
                    message = $"Cancelled asset pack download request '{info.name}'.";
                    break;
                case AndroidAssetPackStatus.WaitingForWifi:
                    AndroidAssetPacks.RequestToUseMobileDataAsync(OnRequestToUseMobileDataComplete);
                    break;
                case AndroidAssetPackStatus.Completed:
                    if(AndroidAssetPacks.GetAssetPackPath(info.name) is string assetPackPath && !string.IsNullOrEmpty(assetPackPath))
                    {
                        // Asset pack was located on device. Proceed with loading the bundle.
                        PlayAssetDeliveryRuntimeData.Instance.AssetPackNameToDownloadPath.Add(info.name,assetPackPath);
                        if(m_ProviderInterfaces.Remove(info.name,out var providerInterface))
                        {
                            foreach(var pi in providerInterface)
                                base.Provide(pi);

                        }
                    }
                    else
                    {
                        message = $"Downloaded asset pack '{info.name}' but cannot locate it on device.";
                    }
                    break;
            }

            if(!string.IsNullOrEmpty(message))
            {
                Debug.LogError(message);
                if(m_ProviderInterfaces.Remove(info.name,out var providerInterface))
                {
                    foreach(var pi in providerInterface)
                        pi.Complete<AssetBundleResource>(null,false,new RemoteProviderException(message));
                }
            }
        }

        /// <inheritdoc/>
        public void Update(float unscaledDeltaTime)
        {
            if (m_AssetPackQueue.Count == 0) {
                return;
            }

#if UNITY_IOS
            var handles = m_AssetPackQueue.SelectMany(assetPack => {
                return m_ProviderInterfaces.TryGetValue(assetPack,out var handleSet) ? handleSet.Select(handle => (assetPack, handle)) : Enumerable.Empty<(string assetPack, ProvideHandle handle)>();
            }).Where(pair => !string.IsNullOrEmpty(pair.handle.Location?.PrimaryKey)).ToList();

            if(handles.Count == 0)
            {
                m_AssetPackQueue.Clear();
                Addressables.ResourceManager.RemoveUpdateReciever(this);
                return;
            }

            var tags = handles.Select(pair => pair.handle.Location.PrimaryKey).Distinct().ToArray();
            OnDemandResources.PreloadAsync(tags).completed += (asyncOp) => {
                var request = asyncOp as OnDemandResourcesRequest;
                if(!string.IsNullOrEmpty(request?.error))
                {
                    var message = $"On-Demand Resource request for tags '{string.Join(", ",tags)}' failed with error: {request.error}";
                    Debug.LogError(message);
                    foreach(var (_, handle) in handles)
                        handle.Complete<AssetBundleResource>(null,false,new RemoteProviderException(message));
                }
                else
                {
                    // ODR request succeeded. Provide the bundles.
                    foreach(var (_, handle) in handles)
                        base.Provide(handle);
                }

                // Clean up provider interfaces for all involved asset packs.
                foreach(var assetPack in handles.Select(handle => handle.assetPack).Distinct())
                    m_ProviderInterfaces.Remove(assetPack);
            };
#else
            AndroidAssetPacks.DownloadAssetPackAsync(m_AssetPackQueue.ToArray(), CheckDownloadStatus);
#endif
            m_AssetPackQueue.Clear();
            Addressables.ResourceManager.RemoveUpdateReciever(this);
        }

        void OnRequestToUseMobileDataComplete(AndroidAssetPackUseMobileDataRequestResult result)
        {
            if (!result.allowed)
            {
                var message = "Request to use mobile data was denied.";
                Debug.LogError(message);
                foreach (var p in m_ProviderInterfaces)
                {
                    foreach (var pi in p.Value)
                    {
                        pi.Complete<AssetBundleResource>(null, false, new RemoteProviderException(message));
                    }
                }
                m_ProviderInterfaces.Clear();
            }
        }

        internal void CompleteInterface(ProvideHandle handle)
        {
            foreach(var pair in m_ProviderInterfaces.Where((pair) => pair.Value.Remove(handle) && pair.Value.Count < 1).ToArray())
                m_ProviderInterfaces.Remove(pair.Key);
        }
#else
        /// <inheritdoc/>
        public void Update(float unscaledDeltaTime)
        {
        }
#endif
    }
}
