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
        }
    }

}
