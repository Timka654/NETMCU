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

            MCUBackend? backend = null;


            if(backend == null)
                return CommandReadStateEnum.Failed;


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

            return await base.ProcessCommand(reader, values);
        }
    }

    internal class Program
    {
        static async Task Main(string[] args)
        {
            CommandLineArgsReader? reader = default;

#if DEBUG

            var projectPath = Path.GetFullPath("../../../../devmcu/devmcu.csproj"); // temp
            if (!File.Exists(projectPath))
                projectPath = Path.GetFullPath("../devmcu/devmcu.csproj");
            if (!File.Exists(projectPath))
                projectPath = Path.GetFullPath("devmcu/devmcu.csproj");

            reader = new CommandLineArgsReader(new CommandLineArgs(new[] { "build", "--path", projectPath }, false));
#else
            reader = new CommandLineArgsReader(new CommandLineArgs());
#endif
            await CLHandler<RootCliHandler>.Instance.ProcessCommand(new CommandLineArgsReader(new CommandLineArgs()));
            //// Example simple arguments trace
            //bool shouldFlash = args.Contains("--flash");
            //ProgrammerType progType = ProgrammerType.STFlash;
            //if (args.Any(a => a.Contains("openocd"))) progType = ProgrammerType.OpenOCD;
            //else if (args.Any(a => a.Contains("cubeprogrammer"))) progType = ProgrammerType.STM32CubeProgrammer;
            //else if (args.Any(a => a.Contains("stlinkcli"))) progType = ProgrammerType.STLinkCLI;
            //else if (args.Any(a => a.Contains("dfu"))) progType = ProgrammerType.DfuUtil;

            // 1. Инициализация MSBuild (нужно вызвать один раз при старте)

            //if (shouldFlash) 
            //{
            //    string binPath = Path.Combine(sc.StartupProject.mcuBinPath, "output.bin");

            //    uint flashBase = 0x08000000;
            //    var flashBaseStr = sc.StartupProject.Options?.Configurations?["FLASH_BASE_ADDRESS"];
            //    if (flashBaseStr != null) {
            //        flashBase = flashBaseStr.StartsWith("0x") ? uint.Parse(flashBaseStr.TrimStart('0', 'x'), System.Globalization.NumberStyles.HexNumber) : uint.Parse(flashBaseStr);
            //    }

            //    Console.WriteLine($"\nFlashing the firmware using {progType}...");
            //    FirmwareFlasher.Flash(binPath, flashBase, progType);
            //}
        }
    }

}
