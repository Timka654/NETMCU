using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Options;
using NETMCUCompiler.CodeBuilder;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NETMCUCompiler
{
    public partial class BuildingContext(string path, BuildingOutputTypeEnum type, SolutionContext solutionContext)
    {
        public string Path { get; private set; } = path;

        public string RootPath { get; private set; }
        public string BinPath { get; private set; }
        public string ObjPath { get; private set; }

        public Compilation Compilation { get; private set; }

        public BuildingOptions? Options { get; private set; }

        public Dictionary<string, long> CoreSymbols { get; private set; }

        public INamedTypeSymbol? ProgramMainType { get; private set; }
        public IMethodSymbol? ProgramMainMethod { get; private set; }
        public MethodDeclarationSyntax? ProgramMainNode { get; private set; }
        public ClassDeclarationSyntax? ProgramMainTypeNode { get; private set; }
        public SemanticModel? ProgramSemanticModel { get; private set; }

        CommonFileModel? commonData;

        bool needsRebuildCore;

        string mcuObjCorePath, mcuCorePath, tempDockerfilePath, dockerContent, commonCorePath, commonOldPath, buildDir, mcuBinPath;


        CompilationContext? compilationContext;
        //LinkerContext? compilationLinker = new LinkerContext();


        IEnumerable<MethodCompilationContext> Methods => compilationContext.GetMethods();

        IEnumerable<MethodCompilationContext> PublicMethods => Methods.Where(m => m.IsPublic);


        ClassDeclarationSyntax? mcuConfigClassDeclaration = null;
        SemanticModel? mcuConfigSemanticModel = null;


        Dictionary<ProjectId, BuildingContext> referenceContexts = new Dictionary<ProjectId, BuildingContext>();

        #region Utils

        private static void DirectoryCopy(string sourceDirName, string destDirName,
                                          bool copySubDirs, string? fileFilter = null, string? rootDir = null)
        {
            rootDir ??= sourceDirName;
            fileFilter ??= "*";
            // Get the subdirectories for the specified directory.
            DirectoryInfo dir = new DirectoryInfo(sourceDirName);

            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException(
                    "Source directory does not exist or could not be found: "
                    + sourceDirName);
            }

            DirectoryInfo[] dirs = dir.GetDirectories();
            // If the destination directory doesn't exist, create it.
            if (!Directory.Exists(destDirName))
            {
                Directory.CreateDirectory(destDirName);
            }

            // Get the files in the directory and copy them to the new location.
            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                if (!Regex.IsMatch(System.IO.Path.GetRelativePath(rootDir, file.FullName), fileFilter))
                    continue;
                string temppath = System.IO.Path.Combine(destDirName, file.Name);
                file.CopyTo(temppath, false);
            }

            // If copying subdirectories, copy them and their contents to new location.
            if (copySubDirs)
            {
                foreach (DirectoryInfo subdir in dirs)
                {
                    string temppath = System.IO.Path.Combine(destDirName, subdir.Name);
                    DirectoryCopy(subdir.FullName, temppath, copySubDirs, fileFilter, rootDir);
                }
            }
        }

        // Метод проверки наследования (рекурсивный)
        bool IsTargetClass(INamedTypeSymbol? _symbol, string targetBase)
        {
            var symbol = _symbol;

            while (symbol != null)
            {
                if (symbol.ToDisplayString() == targetBase && !SymbolEqualityComparer.Default.Equals(symbol, _symbol)) return true;
                symbol = symbol.BaseType;
            }
            return false;
        }

        #endregion
    }

    public record configureRecord(string fname, IMethodSymbol fsymb, Dictionary<string, object> args);

    public record RelocationRecord(MethodCompilationContext Context, int Offset, bool isStatic);
}
