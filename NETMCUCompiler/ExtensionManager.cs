using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using NETMCUCompiler.Shared.Attributes;
using NETMCUCompiler.Shared.Compilation.Backend;
using NETMCUCompiler.Shared;

namespace NETMCUCompiler
{
    public class ExtensionManager
    {
        private static ExtensionManager _instance;
        public static ExtensionManager Instance => _instance ??= new ExtensionManager();

        public Dictionary<string, Type> Backends { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Type> Flashers { get; } = new(StringComparer.OrdinalIgnoreCase);

        private ExtensionManager()
        {
            LoadExtensions();
        }

        private void LoadExtensions()
        {
            var exeDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var dataDirectory = Path.Combine(exeDirectory, "data");

            if (!Directory.Exists(dataDirectory))
            {
                Directory.CreateDirectory(dataDirectory);
            }

            // Load built-in first
            ScanAssembly(Assembly.GetExecutingAssembly());
            // Scan other loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                 ScanAssembly(asm);
            }

            // Scan Plugins
            var dlls = Directory.GetFiles(dataDirectory, "*.dll", SearchOption.AllDirectories);
            foreach (var dll in dlls)
            {
                try
                {
                    // For now use LoadFrom to parse metadata. AssemblyLoadContext might be better in larger scales
                    var assembly = Assembly.LoadFrom(dll);
                    ScanAssembly(assembly);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warning] Failed to load extension {dll}: {ex.Message}");
                }
            }
        }

        private void ScanAssembly(Assembly assembly)
        {
            try
            {
                var types = assembly.GetTypes();
                foreach (var type in types)
                {
                    var backendAttrs = type.GetCustomAttributes<MCUCompilationBackendAttribute>();
                    foreach (var attr in backendAttrs)
                    {
                        if (typeof(MCUBackend).IsAssignableFrom(type))
                        {
                            Backends[attr.Name] = type;
                        }
                    }

                    var flasherAttrs = type.GetCustomAttributes<MCUFirmwareFlasherAttribute>();
                    foreach (var attr in flasherAttrs)
                    {
                        if (typeof(IFirmwareFlasher).IsAssignableFrom(type))
                        {
                            Flashers[attr.Name] = type;
                        }
                    }
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Ignore load errors for reflection
            }
            catch (Exception)
            {
                // Ignore others
            }
        }

        public MCUBackend CreateBackend(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (Backends.TryGetValue(name, out var type))
            {
                return (MCUBackend)Activator.CreateInstance(type);
            }
            return null;
        }

        public IFirmwareFlasher CreateFlasher(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (Flashers.TryGetValue(name, out var type))
            {
                return (IFirmwareFlasher)Activator.CreateInstance(type);
            }
            return null;
        }
    }
}