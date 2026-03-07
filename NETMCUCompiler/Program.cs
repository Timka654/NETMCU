using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NETMCUCompiler
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            var projectPath = Path.GetFullPath("../../../../devmcu/devmcu.csproj"); // temp
            if (!File.Exists(projectPath))
                projectPath = Path.GetFullPath("../devmcu/devmcu.csproj");
            if (!File.Exists(projectPath))
                projectPath = Path.GetFullPath("devmcu/devmcu.csproj");

            // Example simple arguments trace
            bool shouldFlash = args.Contains("--flash");
            ProgrammerType progType = ProgrammerType.STFlash;
            if (args.Any(a => a.Contains("openocd"))) progType = ProgrammerType.OpenOCD;
            else if (args.Any(a => a.Contains("cubeprogrammer"))) progType = ProgrammerType.STM32CubeProgrammer;
            else if (args.Any(a => a.Contains("stlinkcli"))) progType = ProgrammerType.STLinkCLI;

            // 1. Инициализация MSBuild (нужно вызвать один раз при старте)
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();

            SolutionContext sc = new SolutionContext();

            sc.StartupProject = new BuildingContext(projectPath, BuildingOutputTypeEnum.Executable, sc);
            sc.Projects.Add(projectPath, sc.StartupProject);

            await sc.StartupProject.LoadAsync();

            if(!await sc.StartupProject.BuildCore())
            {
                Console.WriteLine("Build failed");
                return;
            }

            if(!await sc.StartupProject.Compile())
            {
                Console.WriteLine("Compile failed");
                return;
            }
            Console.WriteLine("Compile succeeded");

            if (shouldFlash) 
            {
                string binPath = Path.Combine(sc.StartupProject.mcuBinPath, "output.bin");

                uint flashBase = 0x08000000;
                var flashBaseStr = sc.StartupProject.Options?.Configurations?["FLASH_BASE_ADDRESS"];
                if (flashBaseStr != null) {
                    flashBase = flashBaseStr.StartsWith("0x") ? uint.Parse(flashBaseStr.TrimStart('0', 'x'), System.Globalization.NumberStyles.HexNumber) : uint.Parse(flashBaseStr);
                }

                Console.WriteLine($"\nFlashing the firmware using {progType}...");
                FirmwareFlasher.Flash(binPath, flashBase, progType);
            }
        }
    }

}
