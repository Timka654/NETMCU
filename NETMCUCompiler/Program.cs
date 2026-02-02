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
            var projectPath = "../../../../devmcu/devmcu.csproj"; // temp

            // 1. Инициализация MSBuild (нужно вызвать один раз при старте)
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();

            BuildingContext bc = new BuildingContext(projectPath);

            await bc.LoadAsync();

            if(!await bc.BuildCore())
            {
                Console.WriteLine("Build failed");
                return;
            }

            if(!await bc.Compile())
            {
                Console.WriteLine("Compile failed");
                return;
            }
        }
    }

}
