﻿// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using log4net;

namespace EdFi.Ods.Api.Helpers
{
    public static class AssemblyLoaderHelper
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(AssemblyLoaderHelper));
        private const string AssemblyMetadataSearchString = "assemblyMetadata.json";

        public static void LoadAssembliesFromExecutingFolder(bool includeFramework = false)
        {
            // Storage to ensure not loading the same assembly twice and optimize calls to GetAssemblies()
            IDictionary<string, bool> loadedByAssemblyName = new ConcurrentDictionary<string, bool>();

            LoadAssembliesFromExecutingFolder();

            int alreadyLoaded = loadedByAssemblyName.Keys.Count;

            var sw = new Stopwatch();
            _logger.Debug($"Already loaded assemblies:");

            CacheAlreadyLoadedAssemblies(loadedByAssemblyName, includeFramework);

            // Loop on loaded assemblies to load dependencies (it includes Startup assembly so should load all the dependency tree)
            foreach (Assembly nonFrameworkAssemblies in AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => IsNotNetFramework(a.FullName)))
            {
                LoadReferencedAssembly(nonFrameworkAssemblies, loadedByAssemblyName, includeFramework);
            }

            _logger.Debug(
                $"Assemblies loaded after scan ({loadedByAssemblyName.Keys.Count - alreadyLoaded} assemblies in {sw.ElapsedMilliseconds} ms):");

            void LoadAssembliesFromExecutingFolder()
            {
                // Load referenced assemblies into the domain. This is effectively the same as EnsureLoaded in common
                // however the assemblies are linked in the project.
                var directoryInfo = new DirectoryInfo(
                    Path.GetDirectoryName(
                        Assembly.GetExecutingAssembly()
                            .Location));

                _logger.Debug($"Loaded assemblies from executing folder: '{directoryInfo.FullName}'");

                foreach (FileInfo assemblyFilesToLoad in directoryInfo.GetFiles("*.dll")
                    .Where(fi => ShouldLoad(fi.Name, loadedByAssemblyName, includeFramework)))
                {
                    _logger.Debug($"{assemblyFilesToLoad.Name}");
                    Assembly.LoadFrom(assemblyFilesToLoad.FullName);
                }
            }
        }

        private static void LoadReferencedAssembly(Assembly assembly, IDictionary<string, bool> loaded,
            bool includeFramework = false)
        {
            // Check all referenced assemblies of the specified assembly
            foreach (var referencedAssembliesToLoad in assembly.GetReferencedAssemblies()
                .Where(a => ShouldLoad(a.FullName, loaded, includeFramework)))
            {
                // Load the assembly and load its dependencies
                LoadReferencedAssembly(Assembly.Load(referencedAssembliesToLoad), loaded, includeFramework); // AppDomain.CurrentDomain.Load(name)
                loaded.TryAdd(referencedAssembliesToLoad.FullName, true);
                _logger.Debug($"Referenced assembly => '{referencedAssembliesToLoad.FullName}'");
            }
        }

        private static void CacheAlreadyLoadedAssemblies(IDictionary<string, bool> loaded, bool includeFramework = false)
        {
            foreach (var alreadyLoadedAssemblies in AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => ShouldLoad(a.FullName, loaded, includeFramework)))
            {
                loaded.TryAdd(alreadyLoadedAssemblies.FullName, true);
                _logger.Debug($"Assembly '{alreadyLoadedAssemblies.FullName}' was already loaded.");
            }
        }

        private static bool ShouldLoad(string assemblyName, IDictionary<string, bool> loaded, bool includeFramework = false)
        {
            return (includeFramework || IsNotNetFramework(assemblyName))
                   && !loaded.ContainsKey(assemblyName);
        }

        private static bool IsNotNetFramework(string assemblyName)
        {
            return !assemblyName.StartsWithIgnoreCase("Microsoft.")
                   && !assemblyName.StartsWithIgnoreCase("System.")
                   && !assemblyName.StartsWithIgnoreCase("Newtonsoft.")
                   && assemblyName != "netstandard"
                   && !assemblyName.StartsWithIgnoreCase("Autofac");
        }

        // public static IEnumerable<string> FindPluginAssemblies(string pluginFolder, bool includeFramework = false)
        // {
        //     // Storage to ensure not loading the same assembly twice and optimize calls to GetAssemblies()
        //     IDictionary<string, bool> loaded = new ConcurrentDictionary<string, bool>();
        //
        //     CacheAlreadyLoadedAssemblies(loaded, includeFramework);
        //
        //     if (!IsPluginFolderNameSupplied())
        //     {
        //         yield break;
        //     }
        //
        //     pluginFolder = Path.GetFullPath(pluginFolder);
        //
        //     if (!Directory.Exists(pluginFolder))
        //     {
        //         _logger.Debug($"Plugin folder '{pluginFolder}' does not exist. No plugins will be loaded.");
        //         yield break;
        //     }
        //
        //     var assemblies = Directory.GetFiles(pluginFolder, "*.dll", SearchOption.AllDirectories);
        //
        //     var pluginAssemblyLoadContext = new PluginAssemblyLoadContext();
        //
        //     try
        //     {
        //         foreach (var assemblyPath in assemblies)
        //         {
        //             var assembly = pluginAssemblyLoadContext.LoadFromAssemblyPath(assemblyPath);
        //
        //             if (!HasPlugin(assembly))
        //             {
        //                 continue;
        //             }
        //
        //             string assemblyDirectory = Path.GetDirectoryName(assemblyPath);
        //             var assemblyMetadata = ReadAssemblyMetadata(assembly);
        //
        //             if (IsExtensionAssembly(assemblyMetadata))
        //             {
        //                 var validator = GetExtensionValidator();
        //                 var validationResult = validator.ValidateObject(assemblyDirectory);
        //
        //                 if (!validationResult.Any())
        //                 {
        //                     yield return assembly.Location;
        //                 }
        //                 else
        //                 {
        //                     _logger.Warn($"Assembly: {assembly.GetName()} - {string.Join(",", validationResult)}");
        //                 }
        //             }
        //             else if (IsProfileAssembly(assemblyMetadata))
        //             {
        //                 yield return assembly.Location;
        //             }
        //         }
        //     }
        //     finally
        //     {
        //         pluginAssemblyLoadContext.Unload();
        //     }
        //     
        //     static FluentValidationObjectValidator GetExtensionValidator()
        //     {
        //         return new FluentValidationObjectValidator(
        //             new IValidator[]
        //             {
        //                 new ApiModelExistsValidator(),
        //                 new IsExtensionPluginValidator(),
        //                 new IsApiVersionValidValidator(ApiVersionConstants.InformationalVersion)
        //             });
        //     }
        //
        //     bool IsPluginFolderNameSupplied()
        //     {
        //         if (!string.IsNullOrWhiteSpace(pluginFolder))
        //         {
        //             return true;
        //         }
        //
        //         _logger.Debug($"Plugin folder was not specified so no plugins will be loaded.");
        //         return false;
        //     }
        //
        //     static bool HasPlugin(Assembly assembly)
        //     {
        //         return assembly.GetTypes().Any(
        //             t => t.GetInterfaces()
        //                 .Any(i => i.AssemblyQualifiedName == typeof(IPluginMarker).AssemblyQualifiedName));
        //     }
        // }

        // private static bool IsExtensionAssembly(AssemblyMetadata assemblyMetadata)
        // {
        //     return assemblyMetadata.AssemblyModelType.EqualsIgnoreCase(PluginConventions.Extension);
        // }
        //
        // private static bool IsProfileAssembly(AssemblyMetadata assemblyMetadata)
        // {
        //     return assemblyMetadata.AssemblyModelType.EqualsIgnoreCase(PluginConventions.Profile);
        // }

        // private static AssemblyMetadata ReadAssemblyMetadata(Assembly assembly)
        // {
        //     var resourceName = assembly.GetManifestResourceNames()
        //         .FirstOrDefault(p => p.EndsWith(AssemblyMetadataSearchString));
        //
        //     if (resourceName == null)
        //     {
        //         throw new Exception($"Assembly metadata embedded resource '{AssemblyMetadataSearchString}' not found in assembly '{Path.GetFileName(assembly.Location)}'.");
        //     }
        //
        //     var stream = assembly.GetManifestResourceStream(resourceName);
        //
        //     using var reader = new StreamReader(stream);
        //
        //     string jsonFile = reader.ReadToEnd();
        //
        //     _logger.Debug($"Deserializing object type '{typeof(AssemblyMetadata)}' from embedded resource '{resourceName}'.");
        //
        //     return JsonConvert.DeserializeObject<AssemblyMetadata>(jsonFile);
        // }

        // private class PluginAssemblyLoadContext : AssemblyLoadContext
        // {
        //     public PluginAssemblyLoadContext()
        //         : base(isCollectible: true) { }
        // }

        // private class AssemblyMetadata
        // {
        //     public string AssemblyModelType { get; set; }
        //     public string AssemblyMetadataFormatVersion { get; set; }
        // }
    }
}
