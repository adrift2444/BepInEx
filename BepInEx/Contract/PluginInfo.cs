using System;
using System.Collections.Generic;

namespace BepInEx
{
	[Serializable]
	public class PluginInfo
	{
		public BepInPlugin Metadata { get; internal set; }

		public IEnumerable<BepInProcess> Processes { get; internal set; }

		public IEnumerable<BepInDependency> Dependencies { get; internal set; }

		public IEnumerable<BepInIncompatibility> Incompatibilities { get; internal set; }

		[field: NonSerialized]
		public string Location { get; internal set; }

		[field: NonSerialized]
		public BaseUnityPlugin Instance { get; internal set; }

		internal string TypeName { get; set; }
	}
}
