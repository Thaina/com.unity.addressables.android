using System;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Collections.Generic;

using Newtonsoft.Json.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine.AddressableAssets;
using System.Reflection;





#if UNITY_IOS
using UnityEngine.iOS;
#endif
using UnityEngine.ResourceManagement.ResourceLocations;

#if UNITY_IOS

namespace UnityEngine.ResourceManagement.ResourceProviders
{
    public class ODRAssetBundleResource : IAssetBundleResource, IUpdateReceiver
    {
        AssetBundle? m_AssetBundle;
        OnDemandResourcesRequest? m_Request;
        ProvideHandle m_Handle;

        static JArray? _bundles;
        static JArray? Bundles
        {
            get
            {
                if(_bundles == null)
                {
                    try
                    {
                        string odrSettingsRuntimePath = Path.Combine(Addressables.RuntimePath,"settings_odr.json");
                        if(File.Exists(odrSettingsRuntimePath) && File.ReadAllText(odrSettingsRuntimePath) is string json && !string.IsNullOrEmpty(json))
                            _bundles = JObject.Parse(json)?["m_bundles"] as JArray;
                    }
                    catch(Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                return _bundles;
            }
        }

        static Dictionary<string,AssetBundle> alreadyLoaded = new();
        static Dictionary<string,OnDemandResourcesRequest> alreadyRequest = new();
        public async Awaitable Start(ProvideHandle handle)
        {
            m_Handle = handle;

            try
            {
                string primaryKey = m_Handle.Location.PrimaryKey;
                if(m_AssetBundle || (alreadyLoaded.TryGetValue(primaryKey,out m_AssetBundle) && m_AssetBundle))
                {
                    m_Handle.Complete(this,m_AssetBundle != null,null!);
                    return;
                }

                if(primaryKey.Contains("_monoscripts_") || primaryKey.Contains("_unitybuiltinassets_"))
                {
                    Debug.Log("Load base : " + primaryKey);
                    int aaindex = m_Handle.Location.InternalId.IndexOf("Raw/aa/iOS");
                    string location = aaindex >= 0 ? Path.Combine(Application.dataPath,m_Handle.Location.InternalId.Substring(aaindex)) : handle.Location.ToString();
                    Debug.Log("aaindex : " + aaindex + " => " + location);

                    m_AssetBundle = AssetBundle.LoadFromFile(location);
                    if(m_AssetBundle)
                        alreadyLoaded[primaryKey] = m_AssetBundle;

                    m_Handle.Complete(this,m_AssetBundle != null,null!);
                    return;
                }

                if(ODRPath is string odr && !string.IsNullOrEmpty(odr))
                {
                    Debug.Log("ODRPath : " + odr);
                    m_AssetBundle = GetAssetBundle(null,m_Handle.Location);
                    if(m_AssetBundle)
                        m_Handle.Complete(this,m_AssetBundle != null,null!);
                }

                var bundle = Bundles?.OfType<JObject>().FirstOrDefault((jobj) => jobj?["name"]?.ToString() == primaryKey);
                if(bundle?.Get("tag")?.ToString() is not string tag || string.IsNullOrEmpty(tag))
                    throw new Exception("Not supported loading bundle : " + primaryKey + "\n" + bundle?.ToString());

                if(alreadyRequest.TryGetValue(tag,out var request) && request != null)
                {
                    m_Request?.Dispose();
                    m_Request = request;
                }
                else
                {
                    Debug.Log("OnDemandResources.PreloadAsync : " + tag);
                    alreadyRequest[tag] = m_Request = OnDemandResources.PreloadAsync(new string[] { tag });
                }

                while(!m_Request.isDone)
                    await Awaitable.NextFrameAsync();

                var e = string.IsNullOrEmpty(m_Request.error) ? null : new Exception(m_Request.error);
                Debug.Log("ODR request " + tag + " " + (e?.Message ?? "success"));
                if(e != null)
                    throw e;

                Unload();

                m_Handle.Complete(this,GetAssetBundle() != null,null!);
            }
            catch(Exception e)
            {
                m_Handle.Complete(this,false,e);
            }
        }

        public static string? ODRPath => Path.Combine(Application.dataPath,"..","OnDemandResources") is string odr && Directory.Exists(odr) ? Path.GetRelativePath(Environment.CurrentDirectory,odr) : null;

        public static AssetBundle? GetAssetBundle(OnDemandResourcesRequest? request,IResourceLocation location,List<Exception>? exceptions = null)
        {
            IEnumerable<string> IterateResourcePaths()
            {
                if(!Application.isEditor)
                {
                    if(request != null)
                        yield return request.GetResourcePath(location.PrimaryKey);

                    int aaindex = location.InternalId.IndexOf("Raw/aa/iOS");
                    if(aaindex >= 0 && ODRPath is string odr && !string.IsNullOrEmpty(odr))
                    {
                        string fileName = Path.GetFileName(location.InternalId);
                        foreach(var path in Directory.EnumerateFileSystemEntries(odr,fileName,new EnumerationOptions() { RecurseSubdirectories = true }))
                            yield return path;
                    }

                    yield return location.PrimaryKey;
                }

                yield return location.ToString();
            }

            return IterateResourcePaths().Select((path) => {
                try
                {
                    if(!string.IsNullOrEmpty(path))
                        return AssetBundle.LoadFromFile(path);
                }
                catch(Exception e)
                {
                    exceptions?.Add(e);
                }

                return null;
            }).FirstOrDefault((item) => item != null);
        }

        public AssetBundle GetAssetBundle()
        {
            if(!m_AssetBundle)
            {
                var exceptions = new List<Exception>();
                var m_AssetBundle = GetAssetBundle(m_Request,m_Handle.Location,exceptions);
                Debug.LogFormat("Loaded : {0}",m_AssetBundle ? m_AssetBundle : ("Fail " + m_Handle.Location));
                if(m_AssetBundle)
                    alreadyLoaded[m_Handle.Location.PrimaryKey] = m_AssetBundle;
                else if(exceptions.Count > 0)
                    throw exceptions.Count > 1 ? new AggregateException(exceptions) : exceptions.FirstOrDefault();
            }

            return m_AssetBundle;
        }

        public void Unload()
        {
            if(m_AssetBundle)
                m_AssetBundle.Unload(true);
            m_AssetBundle = null;
        }

        public void Update(float unscaledDeltaTime)
        {
            // Check for errors
            if (m_Request.error != null)
                throw new Exception("ODR request failed: " + m_Request.error);
        }
    }

    [DisplayName("ODR AssetBundle Provider")]
    public class ODRBundleProvider : ResourceProviderBase
    {
        /// <inheritdoc/>
        public override void Provide(ProvideHandle providerInterface)
        {
            new ODRAssetBundleResource().Start(providerInterface);
        }

        /// <inheritdoc/>
        public override Type GetDefaultType(IResourceLocation location)
        {
            return typeof(IAssetBundleResource);
        }

        /// <summary>
        /// Releases the asset bundle via AssetBundle.Unload(true).
        /// </summary>
        /// <param name="location">The location of the asset to release</param>
        /// <param name="asset">The asset in question</param>
        public override void Release(IResourceLocation location, object asset)
        {
            if (location == null)
                throw new ArgumentNullException("location");

            if(asset == null)
            {
                Debug.LogWarningFormat("Releasing null asset bundle from location {0}.  This is an indication that the bundle failed to load.",location);
                return;
            }

            if(asset is ODRAssetBundleResource bundle)
                bundle.Unload();
        }
    }
}

#else

namespace UnityEngine.ResourceManagement.ResourceProviders
{
    [DisplayName("ODR AssetBundle Provider")]
    public class ODRBundleProvider : AssetBundleProvider
    {}
}

#endif