#if UNITY_EDITOR || UNITY_ANDROID || UNITY_IOS
using System;
using System.Collections.Generic;
using UnityEngine.Serialization;

namespace UnityEngine.AddressableAssets.Android
{
    /// <summary>
    /// Custom asset pack information.
    /// </summary>
    [Serializable]
    public class CustomAssetPackDataEntry
    {
        [field:SerializeField,FormerlySerializedAs("m_AssetPackName")]
        /// <summary>
        /// Asset pack name.
        /// </summary>
        public string AssetPackName { get; private set; }

        [field:SerializeField,FormerlySerializedAs("m_DeliveryType")]
        /// <summary>
        /// Asset pack delivery type.
        /// </summary>
        public DeliveryType DeliveryType { get; private set; }

        /// <summary>
        /// List of all asset bundles which are assigned to this asset pack.
        /// </summary>
        public List<string> AssetBundles;

        /// <summary>
        /// Create a new CustomAssetPackDataEntry object.
        /// </summary>
        /// <param name="assetPackName">Asset pack name.</param>
        /// <param name="deliveryType">Asset pack delivery type.</param>
        /// <param name="assetBundles">Asset bundles which are assigned to this asset pack.</param>
        public CustomAssetPackDataEntry(string assetPackName, DeliveryType deliveryType, IEnumerable<string> assetBundles)
        {
            AssetPackName = assetPackName;
            DeliveryType = deliveryType;
            AssetBundles = new List<string>(assetBundles);
        }
    }

    [Serializable]
    internal class CustomAssetPackData
    {
        [SerializeField]
        internal List<CustomAssetPackDataEntry> Entries;

        public CustomAssetPackData(IEnumerable<CustomAssetPackDataEntry> entries)
        {
            Entries = new List<CustomAssetPackDataEntry>(entries);
        }
    }
}
#endif
