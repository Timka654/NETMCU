using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NETMCUCompiler.Shared.Compilation.Backend;
using NSL.Utils.CommandLine;
using NSL.Utils.CommandLine.CLHandles;
using NSL.Utils.CommandLine.CLHandles.Arguments;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NETMCUCompiler
{
    public class RootCliHandler : NSL.Utils.CommandLine.CLHandles.CLHandler
    {
        public RootCliHandler() {
           base.AddCommands(base.SelectSubCommands<CLHandleSelectAttribute>("root", true));
        }
    }

    [CLHandleSelect("root")]
    [CLArgument("path", typeof(string), Description = "The path to the project .csproj file")]
    [CLArgument("backend", typeof(string), Description = "The backend to use for building")]
    [CLArgument("flash", typeof(CLContainsType), true, Description = "Flag for flash after build")]
    [CLArgument("flasher", typeof(string), true, Description = "The flasher to use for flashing")]
    [CLArgument("flash-port", typeof(ushort), true, Description = "The flash port to use for flashing")]
    [CLArgument("output-type", typeof(string), true, Description = "The output type for the build")]
    public class BuildCliHandler : NSL.Utils.CommandLine.CLHandles.CLHandler
    {
        public override string Command => "build";

        [CLArgumentValue("path")] public string Path { get; set; }
        [CLArgumentValue("backend")] public string Backend { get; set; }
        [CLArgumentExists("flash")] public bool Flash { get; set; }

        [CLArgumentValue("flasher")] public string Flasher { get; set; }
        [CLArgumentValue("flash-port")] public ushort FlashPort { get; set; }
        [CLArgumentValue("output-type", nameof(BuildingOutputTypeEnum.Executable))] public string OutputType { get; set; }

        public override async Task<CommandReadStateEnum> ProcessCommand(CommandLineArgsReader reader, CLArgumentValues values)
        {
            base.ProcessingAutoArgs(values);

            if (!Enum.TryParse<BuildingOutputTypeEnum>(OutputType, true, out var _outputType))
                return CommandReadStateEnum.Failed;

            MCUBackend? backend = ExtensionManager.Instance.CreateBackend(Backend);

            if(backend == null)
            {
                Console.WriteLine($"Backend '{Backend}' not found. Available backends: " + string.Join(", ", ExtensionManager.Instance.Backends.Keys));
                return CommandReadStateEnum.Failed;
            }


            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();

            SolutionContext sc = new SolutionContext();

            sc.StartupProject = new BuildingContext(Path, _outputType, sc, backend);

            sc.Projects.Add(Path, sc.StartupProject);


            await sc.StartupProject.LoadAsync();

            if (!await sc.StartupProject.BuildCore())
            {
                Console.WriteLine("Build failed");
                return CommandReadStateEnum.Failed;
            }

            if (!await sc.StartupProject.Compile())
            {
                Console.WriteLine("Compile failed");
                return CommandReadStateEnum.Failed;
            }

            Console.WriteLine("Compile succeeded");

            if (Flash)
            {
                var flasherStr = string.IsNullOrEmpty(Flasher) ? "st-flash" : Flasher; // default example
                var flasher = ExtensionManager.Instance.CreateFlasher(flasherStr);

                if (flasher != null)
                {
                    string binPath = System.IO.Path.Combine(sc.StartupProject.mcuBinPath, "output.bin");
                    uint flashBase = 0x08000000;
                    var flashBaseStr = sc.StartupProject.Options?.Configurations?["FLASH_BASE_ADDRESS"];
                    if (flashBaseStr != null) {
                        flashBase = flashBaseStr.StartsWith("0x") ? uint.Parse(flashBaseStr.TrimStart('0', 'x'), System.Globalization.NumberStyles.HexNumber) : uint.Parse(flashBaseStr);
                    }

                    if (await flasher.FlashAsync(binPath, flashBase, FlashPort.ToString()))
                    {
                        Console.WriteLine("Flash succeeded");
                    }
                    else
                    {
                        Console.WriteLine("Flash failed");
                    }
                }
                else
                {
                    Console.WriteLine($"Flasher '{flasherStr}' not found.");
                }
            }

            return await base.ProcessCommand(reader, values);
        }
    }

    [CLHandleSelect("root")]
    [CLArgument("flasher", typeof(string), Description = "The flasher to use for flashing")]
    [CLArgument("path", typeof(string), Description = "Path to the generic .bin to flash")]
    [CLArgument("address", typeof(string), true, Description = "Flash base address (e.g. 0x08000000)")]
    [CLArgument("port", typeof(string), true, Description = "Flash port")]
    public class FlashCliHandler : NSL.Utils.CommandLine.CLHandles.CLHandler
    {
        public override string Command => "flash";

        [CLArgumentValue("flasher")] public string Flasher { get; set; }
        [CLArgumentValue("path")] public string Path { get; set; }
        [CLArgumentValue("address")] public string Address { get; set; }
        [CLArgumentValue("port")] public string Port { get; set; }

        public override async Task<CommandReadStateEnum> ProcessCommand(CommandLineArgsReader reader, CLArgumentValues values)
        {
            base.ProcessingAutoArgs(values);

            var flasher = ExtensionManager.Instance.CreateFlasher(Flasher);
            if (flasher == null)
            {
                Console.WriteLine($"Flasher '{Flasher}' not found. Available flashers: " + string.Join(", ", ExtensionManager.Instance.Flashers.Keys));
                return CommandReadStateEnum.Failed;
            }

            uint flashBase = 0x08000000;
            if (!string.IsNullOrEmpty(Address))
            {
                flashBase = Address.StartsWith("0x") ? uint.Parse(Address.TrimStart('0', 'x'), System.Globalization.NumberStyles.HexNumber) : uint.Parse(Address);
            }

            if (await flasher.FlashAsync(Path, flashBase, Port))
            {
                Console.WriteLine("Flash succeeded!");
            }
            else
            {
                Console.WriteLine("Flash failed.");
            }

            return await base.ProcessCommand(reader, values);
        }
    }

    [CLHandleSelect("root")]
    [CLArgument("type", typeof(string), Description = "Type of install (nuget, git, zip, curl...)")]
    [CLArgument("source", typeof(string), Description = "Source locator (URL, package name, etc.)")]
    public class InstallCliHandler : NSL.Utils.CommandLine.CLHandles.CLHandler
    {
        public override string Command => "install";

        [CLArgumentValue("type")] public string Type { get; set; }
        [CLArgumentValue("source")] public string Source { get; set; }

        public override async Task<CommandReadStateEnum> ProcessCommand(CommandLineArgsReader reader, CLArgumentValues values)
        {
            base.ProcessingAutoArgs(values);

            Console.WriteLine($"Installing extension from {Type}: {Source}");
            // TODO: implement logic
            Console.WriteLine("Not implemented yet.");

            return await base.ProcessCommand(reader, values);
        }
    }

    internal class Program
    {
        static async Task Main(string[] args)
        {
            CommandLineArgsReader? reader = default;

#if DEBUG
            if (args != null && args.Length > 0)
            {
                reader = new CommandLineArgsReader(new CommandLineArgs(args, false));
            }
            else
            {
                var projectPath = Path.GetFullPath("../../../../devmcu/devmcu.csproj"); // temp
                if (!File.Exists(projectPath))
                    projectPath = Path.GetFullPath("../devmcu/devmcu.csproj");
                if (!File.Exists(projectPath))
                    projectPath = Path.GetFullPath("devmcu/devmcu.csproj");

                // Provide cortex-m4 as default backend for testing
                reader = new CommandLineArgsReader(new CommandLineArgs(new[] { "build", "--path", projectPath, "--backend", "cortex-m4" }, false));
            }
#else
            reader = new CommandLineArgsReader(new CommandLineArgs(args ?? new string[0], false));
#endif
            await CLHandler<RootCliHandler>.Instance.ProcessCommand(reader.Value);
        }
    }

}
