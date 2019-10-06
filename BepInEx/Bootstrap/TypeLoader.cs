using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using Mono.Cecil;

namespace BepInEx.Bootstrap
{
	/// <summary>
	/// A cached assembly.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	[Serializable]
	public class CachedAssembly<T> where T : new()
	{
		/// <summary>
		/// List of cached items inside the assembly.
		/// </summary>
		public List<T> CacheItems { get; set; }


		/// <summary>
		/// Timestamp of the assembly. Used to check the age of the cache.
		/// </summary>
		public long Timestamp { get; set; }
	}

	/// <summary>
	///     Provides methods for loading specified types from an assembly.
	/// </summary>
	public static class TypeLoader
	{
		private static readonly DefaultAssemblyResolver resolver;
		private static readonly ReaderParameters readerParameters;

		static TypeLoader()
		{
			resolver = new DefaultAssemblyResolver();
			readerParameters = new ReaderParameters { AssemblyResolver = resolver };

			resolver.ResolveFailure += (sender, reference) =>
			{
				var name = new AssemblyName(reference.FullName);

				if (Utility.TryResolveDllAssembly(name, Paths.BepInExAssemblyDirectory, readerParameters, out var assembly) || Utility.TryResolveDllAssembly(name, Paths.PluginPath, readerParameters, out assembly) || Utility.TryResolveDllAssembly(name, Paths.ManagedPath, readerParameters, out assembly))
					return assembly;

				return AssemblyResolve?.Invoke(sender, reference);
			};
		}

		public static event AssemblyResolveEventHandler AssemblyResolve;

		/// <summary>
		///     Looks up assemblies in the given directory and locates all types that can be loaded and collects their metadata.
		/// </summary>
		/// <typeparam name="T">The specific base type to search for.</typeparam>
		/// <param name="directory">The directory to search for assemblies.</param>
		/// <param name="typeSelector">A function to check if a type should be selected and to build the type metadata.</param>
		/// <param name="assemblyFilter">A filter function to quickly determine if the assembly can be loaded.</param>
		/// <param name="cacheName">The name of the cache to get cached types from.</param>
		/// <returns>A list of all loadable type metadatas indexed by the full path to the assembly that contains the types.</returns>
		public static Dictionary<string, List<T>> FindPluginTypes<T>(string directory, Func<TypeDefinition, T> typeSelector, Func<AssemblyDefinition, bool> assemblyFilter = null, string cacheName = null) where T : new()
		{
			var result = new Dictionary<string, List<T>>();
			Dictionary<string, CachedAssembly<T>> cache = null;

			if (cacheName != null)
				cache = LoadAssemblyCache<T>(cacheName);

			foreach (string dll in Directory.GetFiles(Path.GetFullPath(directory), "*.dll", SearchOption.AllDirectories))
				try
				{
					if (cache != null && cache.TryGetValue(dll, out var cacheEntry))
					{
						long lastWrite = File.GetLastWriteTimeUtc(dll).Ticks;
						if (lastWrite == cacheEntry.Timestamp)
						{
							result[dll] = cacheEntry.CacheItems;
							continue;
						}
					}

					var ass = AssemblyDefinition.ReadAssembly(dll, readerParameters);

					if (!assemblyFilter?.Invoke(ass) ?? false)
					{
						ass.Dispose();
						continue;
					}

					var matches = ass.MainModule.Types.Select(typeSelector).Where(t => t != null).ToList();

					if (matches.Count == 0)
					{
						ass.Dispose();
						continue;
					}

					result[dll] = matches;
					ass.Dispose();
				}
				catch (Exception e)
				{
					Logger.LogError(e.ToString());
				}

			if (cacheName != null)
				SaveAssemblyCache(cacheName, result);

			return result;
		}

		/// <summary>
		///     Loads an index of type metadatas from a cache.
		/// </summary>
		/// <param name="cacheName">Name of the cache</param>
		/// <typeparam name="T">Cacheable item</typeparam>
		/// <returns>Cached type metadatas indexed by the path of the assembly that defines the type. If no cache is defined, return null.</returns>
		public static Dictionary<string, CachedAssembly<T>> LoadAssemblyCache<T>(string cacheName) where T : new()
		{
			if (!EnableAssemblyCache.Value)
				return null;

			if (!typeof(T).IsSerializable && !typeof(ISerializable).IsAssignableFrom(typeof(T)))
				return null;

			var result = new Dictionary<string, CachedAssembly<T>>();
			try
			{
				string path = Path.Combine(Paths.CachePath, $"{cacheName}_typeloader.dat");
				if (!File.Exists(path))
					return null;
				using (var fs = File.OpenRead(path))
					return (Dictionary<string, CachedAssembly<T>>)new BinaryFormatter().Deserialize(fs);
			}
			catch (Exception e)
			{
				Logger.LogWarning($"Failed to load cache \"{cacheName}\"; skipping loading cache. Reason: {e.Message}.");
			}

			return result;
		}

		/// <summary>
		///     Saves indexed type metadata into a cache.
		/// </summary>
		/// <param name="cacheName">Name of the cache</param>
		/// <param name="entries">List of plugin metadatas indexed by the path to the assembly that contains the types</param>
		/// <typeparam name="T">Cacheable item</typeparam>
		public static void SaveAssemblyCache<T>(string cacheName, Dictionary<string, List<T>> entries) where T : new()
		{
			if (!EnableAssemblyCache.Value)
				return;
			if (!typeof(T).IsSerializable && !typeof(ISerializable).IsAssignableFrom(typeof(T)))
				return;

			try
			{
				if (!Directory.Exists(Paths.CachePath))
					Directory.CreateDirectory(Paths.CachePath);

				string path = Path.Combine(Paths.CachePath, $"{cacheName}_typeloader.dat");
				var cachedEntries = entries.ToDictionary(kv => kv.Key, kv => new CachedAssembly<T> { CacheItems = kv.Value, Timestamp = File.GetLastWriteTimeUtc(kv.Key).Ticks });

				using (var fs = File.OpenWrite(path))
					new BinaryFormatter().Serialize(fs, cachedEntries);
			}
			catch (Exception e)
			{
				Logger.LogWarning($"Failed to save cache \"{cacheName}\"; skipping saving cache. Reason: {e.Message}.");
			}
		}

		/// <summary>
		///     Converts TypeLoadException to a readable string.
		/// </summary>
		/// <param name="ex">TypeLoadException</param>
		/// <returns>Readable representation of the exception</returns>
		public static string TypeLoadExceptionToString(ReflectionTypeLoadException ex)
		{
			var sb = new StringBuilder();
			foreach (var exSub in ex.LoaderExceptions)
			{
				sb.AppendLine(exSub.Message);
				if (exSub is FileNotFoundException exFileNotFound)
				{
					if (!string.IsNullOrEmpty(exFileNotFound.FusionLog))
					{
						sb.AppendLine("Fusion Log:");
						sb.AppendLine(exFileNotFound.FusionLog);
					}
				}
				else if (exSub is FileLoadException exLoad)
				{
					if (!string.IsNullOrEmpty(exLoad.FusionLog))
					{
						sb.AppendLine("Fusion Log:");
						sb.AppendLine(exLoad.FusionLog);
					}
				}

				sb.AppendLine();
			}

			return sb.ToString();
		}

		#region Config

		private static readonly ConfigEntry<bool> EnableAssemblyCache = ConfigFile.CoreConfig.AddSetting("Caching", "EnableAssemblyCache", true, "Enable/disable assembly metadata cache\nEnabling this will speed up discovery of plugins and patchers by caching the metadata of all types BepInEx discovers.");

		#endregion
	}
}