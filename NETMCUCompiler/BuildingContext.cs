using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using NETMCUCompiler.CodeBuilder;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NETMCUCompiler
{
    internal class BuildingContext(string path)
    {
        public string Path { get; private set; } = path;

        public string BinPath { get; private set; }
        public string ObjPath { get; private set; }

        public Compilation Compilation { get; private set; }

        public BuildingOptions? Options { get; private set; }

        public Dictionary<string, long> CoreSymbols { get; private set; }

        public INamedTypeSymbol? ProgramMainType { get; private set; }
        public IMethodSymbol? ProgramMainMethod { get; private set; }
        public MethodDeclarationSyntax? ProgramMainNode { get; private set; }

        public async Task LoadAsync()
        {
            if (Options != null)
                throw new Exception("Building context is already loaded");

            if (Directory.Exists(path))
            {
                var projectPaths = Directory.GetFiles(path, "*.csproj", SearchOption.TopDirectoryOnly);

                if (projectPaths.Length > 1)
                {
                    throw new Exception($"Target folder has multiple project files[\r\n{string.Join(Environment.NewLine, projectPaths)}\r\n]. Please set target full path");
                }
            }

            if (System.IO.Path.GetExtension(Path) != ".csproj")
            {
                throw new Exception("Building context path must point to .csproj file or folder with single .csproj file");
            }

            await TryLoad();
        }

        public async Task<bool> BuildCore()
        {
            var bc = this;
            var bo = Options;

            if (!bo.replaceable.TryGetValue("CORE_PATH", out var corePath))
                throw new Exception("CORE_PATH is not defined in building options.");

            if (Directory.Exists(corePath))
                Console.WriteLine($"Core path exists: {corePath}");
            else
#if DEBUG
                corePath = "E:\\my_dev\\devmcu\\container\\core";
#else
                throw new Exception($"Core path does not exist: {corePath}");
#endif

            var mcuCorePath = System.IO.Path.Combine(corePath, "mcu");

            StringBuilder dockerContentBuilder = new StringBuilder();

            var dockerFilePath = System.IO.Path.Combine(mcuCorePath, "Dockerfile.netmcu");

            if (File.Exists(dockerFilePath))
                dockerContentBuilder.AppendLine(File.ReadAllText(dockerFilePath));

            if (bo.replaceable.TryGetValue("MCU_TYPE", out var mcu_type))
            {
                mcuCorePath = System.IO.Path.Combine(mcuCorePath, mcu_type);

                dockerFilePath = System.IO.Path.Combine(mcuCorePath, "Dockerfile.netmcu");

                if (File.Exists(dockerFilePath))
                    dockerContentBuilder.AppendLine(File.ReadAllText(dockerFilePath));
                else
                    throw new Exception($"MCU specific Dockerfile not found: {dockerFilePath}");
            }
            else
                throw new Exception("MCU_TYPE is not defined in building options.");

            if (!bo.replaceable.TryAdd("CFLAGS", string.Empty))
                bo.replaceable["CFLAGS"] = bo.replaceable["CFLAGS"].Trim();

            bo.replaceable["CFLAGS_DEFINES"] = string.Join(" ", bo.defines.Select(d =>
            {
                var f = d.Key;
                if (!string.IsNullOrWhiteSpace(d.Value))
                    f += $"={d.Value}";
                return $"-D{f}";
            }));

            bo.replaceable["CFLAGS"] = $"{bo.replaceable["CFLAGS"].Trim()} {bo.replaceable["CFLAGS_DEFINES"]}";

            bo.replaceable["CFLAGS_INCLUDES"] = string.Join(" ", bo.include.Select(inc => $"-I/project/{inc}"));

            bo.replaceable["CFLAGS"] = $"{bo.replaceable["CFLAGS"].Trim()} {bo.replaceable["CFLAGS_INCLUDES"]}";

            bo.replaceable["CFLAGS_MCU"] = bo.replaceable.TryGetValue("MCU", out var _mcu) ? $"-mcpu={_mcu}" : "";

            bo.replaceable["CFLAGS"] = $"{bo.replaceable["CFLAGS"].Trim()} {bo.replaceable["CFLAGS_MCU"]}";

            bo.replaceable["CFLAGS_OPTIMIZATION"] = bo.replaceable.TryGetValue("OPTIMIZATION", out var _optimization) ? $"-{_optimization}" : "-O2";

            bo.replaceable["CFLAGS"] = $"{bo.replaceable["CFLAGS"].Trim()} {bo.replaceable["CFLAGS_OPTIMIZATION"]}";

            bo.replaceable["CFLAGS_STARTUP_ADDRESS"] = bo.replaceable.TryGetValue("STARTUP_ADDRESS", out var _startup_address) ? $"-DUSER_CODE_ADDR={_startup_address}" : "";

            bo.replaceable["CFLAGS"] = $"{bo.replaceable["CFLAGS"].Trim()} {bo.replaceable["CFLAGS_STARTUP_ADDRESS"]}";

            bo.replaceable["CFLAGS"] = bo.replaceable["CFLAGS"].Trim();

            var dockerContent = dockerContentBuilder.ToString();


            var commonCorePath = System.IO.Path.Combine(mcuCorePath, "common.netmcu");

            if (!File.Exists(commonCorePath))
                throw new Exception($"Common core file not found: {commonCorePath}");

            var commonData = JsonSerializer.Deserialize<CommonFileModel>(File.ReadAllText(commonCorePath), JsonSerializerOptions.Web);

            foreach (var req in commonData.Parameters.RequiredValues)
            {
                if (!bo.replaceable.ContainsKey(req))
                    throw new Exception($"Required value '{req}' is not defined in building options.");
            }
            foreach (var def in commonData.Parameters.DefaultValues)
            {
                if (!bo.replaceable.ContainsKey(def.Key))
                    bo.replaceable[def.Key] = def.Value;
            }

            foreach (var kvp in bo.replaceable)
            {
                dockerContent = dockerContent.Replace($"%#{kvp.Key}#%", kvp.Value);
            }

            var objCorePath = System.IO.Path.Combine(bc.ObjPath, "NETMCU");

            if (!Directory.Exists(objCorePath))
                Directory.CreateDirectory(objCorePath);

            var mcuObjCorePath = System.IO.Path.Combine(objCorePath, "mcu_core", mcu_type);

            if (!Directory.Exists(mcuObjCorePath))
                Directory.CreateDirectory(mcuObjCorePath);

            bool needsRebuild = false;

            // Генерируем временный Dockerfile
            var tempDockerfilePath = System.IO.Path.Combine(mcuObjCorePath, "Dockerfile");

            string oldDockerfileContent = "";

            if (File.Exists(tempDockerfilePath))
            {
                oldDockerfileContent = File.ReadAllText(tempDockerfilePath);
                needsRebuild = dockerContent != oldDockerfileContent;
            }
            else
                needsRebuild = true;

            var commonOldPath = System.IO.Path.Combine(mcuObjCorePath, "common.netmcu");

            string commonOldContent = "";

            if (File.Exists(commonOldPath))
            {
                commonOldContent = File.ReadAllText(commonOldPath);

                needsRebuild = needsRebuild || (File.ReadAllText(commonOldPath) != File.ReadAllText(commonCorePath));
            }
            else
                needsRebuild = true;

            var buildDir = System.IO.Path.Combine(mcuObjCorePath, "build");

            needsRebuild = needsRebuild || !Directory.Exists(buildDir);

            if (needsRebuild)
            {
                Directory.Delete(mcuObjCorePath, true);
                Directory.CreateDirectory(mcuObjCorePath);

                DirectoryCopy(mcuCorePath, mcuObjCorePath, true, "(?<!\\.netmcu)$");

                File.WriteAllText(tempDockerfilePath, dockerContent);

                Regex parameterProcessing = new Regex($"%#(\\S+)#%");

                foreach (var path in commonData.Parameters.ProcessingPathes)
                {
                    var files = Directory.GetFiles(mcuObjCorePath, path);

                    foreach (var file in files)
                    {
                        var content = File.ReadAllText(file);
                        var newContent = parameterProcessing.Replace(content, match =>
                        {
                            var key = match.Value.Substring(2, match.Value.Length - 4).Trim();
                            if (bo.replaceable.TryGetValue(key, out var value))
                                return value;
                            else
                                return match.Value; // Оставляем без изменений, если не найдено
                        });
                        File.WriteAllText(file, newContent);
                    }

                }

                // Собираем образ с тегом 'final-mcu' из сгенерированного Dockerfile
                var buildArgs = $"build -t final-mcu -f \"{tempDockerfilePath}\" \"{mcuObjCorePath}\"";
                var buildProcess = Process.Start("docker", buildArgs);
                buildProcess.WaitForExit();

                if (buildProcess.ExitCode != 0)
                {
                    throw new Exception("Failed to build Docker image 'final-mcu'. Check your Dockerfile.");
                }

                Console.WriteLine("Building Core...");

                var coreBuildCmd = $"run --rm -v \"{mcuObjCorePath}:/project\" final-mcu";
                // Запуск (исправил кавычки для безопасности в shell)
                var process = Process.Start("docker", coreBuildCmd);

                process.WaitForExit();

                if (process.ExitCode != 0)
                    throw new Exception("Failed to build MCU core inside Docker container.");

                File.Copy(commonCorePath, commonOldPath, true);

                Console.WriteLine($"Ядро собрано.");
            }

            // После сборки парсим результат
            CoreSymbols = Stm32MapParser.ParseSymbols(System.IO.Path.Combine(buildDir, "kernel.map"));

            if (!CoreSymbols.ContainsKey("main"))
                return false;

            Console.WriteLine($"Точка входа ядра: 0x{CoreSymbols["main"]:X}");

            return true;
        }

        public async Task<bool> Compile()
        {
            Stm32MethodBuilder e = new Stm32MethodBuilder();

            var t = e.BuildAsm(ProgramMainNode);

            return true;
        }


        private async Task TryLoad()
        {
            ClassDeclarationSyntax? classDeclaration = null;
            INamedTypeSymbol? classSymbol;
            SemanticModel? classSemanticModel = null;

            using (var workspace = MSBuildWorkspace.Create())
            {
                // 2. Загружаем проект напрямую
                var project = await workspace.OpenProjectAsync(Path);

                var msbuildProject = new Microsoft.Build.Evaluation.Project(project.FilePath);

                string outDir = msbuildProject.GetPropertyValue("OutDir"); // Путь к bin
                string intermediateDir = msbuildProject.GetPropertyValue("IntermediateOutputPath"); // Путь к obj
                ObjPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(project.FilePath, "..", intermediateDir));
                BinPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(project.FilePath, "..", outDir));

                ProjectCollection.GlobalProjectCollection.UnloadProject(msbuildProject);

                var compilation = await project.GetCompilationAsync();

                if (compilation == null) return;

                Compilation = compilation;

                // 3. Ищем классы во всех файлах проекта
                foreach (var tree in compilation.SyntaxTrees)
                {
                    var semanticModel = compilation.GetSemanticModel(tree);
                    var root = await tree.GetRootAsync();

                    var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

                    foreach (var classDecl in classes)
                    {
                        var symbol = semanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;

                        if (classDecl.Identifier.Text == "Program")
                        {
                            var pmn = classDecl.DescendantNodes().OfType<MethodDeclarationSyntax>()
                                .Where(x => x.Modifiers.Any(m => m.Text == "static") && x.Identifier.Text == "Main")
                                .FirstOrDefault();

                            if (pmn != null)
                            {
                                var pmm = symbol.GetMembers()
                                        .Where(x => x.Name == "Main" && x.IsStatic)
                                        .Cast<IMethodSymbol>()
                                        .FirstOrDefault();
                                if (pmm != null)
                                {
                                    ProgramMainMethod = pmm;
                                    ProgramMainNode = pmn;
                                    ProgramMainType = symbol;
                                }
                            }
                        }

                        // Проверяем наследование от вашего базового класса в NETMCUCore
                        if (IsTargetClass(symbol, "System.MCU.Compiler.ConfigureEntry"))
                        {
                            if (classSemanticModel != null)
                                throw new Exception("Multiple configuration classes found in project. Only one configuration class is allowed.");

                            classDeclaration = classDecl;
                            classSymbol = symbol;
                            classSemanticModel = semanticModel;
                            Console.WriteLine($"Найдена конфигурация: {symbol.Name} в файле {tree.FilePath}");
                            // Здесь вызывайте вашу логику извлечения аргументов
                        }
                    }
                }


                if (classSemanticModel == null)
                    throw new Exception("No configuration class found in project. You must have single class intherits from System.MCU.Compiler.ConfigureEntry");


                Options = new BuildingOptions();

                SemanticMethodExtractor e = new SemanticMethodExtractor();
                var configureMethods = e.ExtractInvocations(classDeclaration, classSemanticModel).ToArray();

                await LoadOptions(configureMethods, classSemanticModel);
            }
        }

        private async Task LoadOptions(configureRecord[] configureMethods, SemanticModel semanticModel)
        {
            var bo = Options!;

            foreach (var item in configureMethods)
            {
                var ma = item.fsymb.GetAttributes();

                foreach (var _ma in ma)
                {
                    if (_ma.AttributeClass.BaseType?.ToDisplayString() != "System.MCU.Compiler.Attributes.MCUConfigurationValueAttribute")
                        continue;

                    var na = _ma.NamedArguments;

                    if (_ma.AttributeClass.ToDisplayString() == "System.MCU.Compiler.Attributes.ReplaceConfigurationValueAttribute")
                    {
                        var name = na.FirstOrDefault(x => x.Key == "Name").Value.Value?.ToString();

                        if (name == default)
                        {
                            name = na.FirstOrDefault(x => x.Key == "NameArg").Value.Value?.ToString();

                            var arg = item.args[name];

                            name = semanticModel.GetConstantValue(arg as ExpressionSyntax).ToString();
                        }

                        var value = na.FirstOrDefault(x => x.Key == "Value").Value.Value?.ToString();

                        if (value == default)
                        {
                            value = na.FirstOrDefault(x => x.Key == "ValueArg").Value.Value?.ToString();

                            var arg = item.args[value];

                            value = semanticModel.GetConstantValue(arg as ExpressionSyntax).ToString();
                        }

                        bo.replaceable[name] = value;

                        continue;
                    }

                    if (_ma.AttributeClass.ToDisplayString() == "System.MCU.Compiler.Attributes.IncludeConfigurationValueAttribute")
                    {
                        var include = na.FirstOrDefault(x => x.Key == "Include").Value.Value?.ToString();

                        if (include == default)
                        {
                            include = na.FirstOrDefault(x => x.Key == "IncludeArg").Value.Value?.ToString();

                            var arg = item.args[include];

                            include = semanticModel.GetConstantValue(arg as ExpressionSyntax).ToString();
                        }

                        if (!bo.include.Contains(include))
                            bo.include.Add(include);

                        continue;
                    }

                    if (_ma.AttributeClass.ToDisplayString() == "System.MCU.Compiler.Attributes.DriveConfigurationValueAttribute")
                    {
                        var path = na.FirstOrDefault(x => x.Key == "Path").Value.Value?.ToString();

                        if (path == default)
                        {
                            path = na.FirstOrDefault(x => x.Key == "PathArg").Value.Value?.ToString();

                            var arg = item.args[path];

                            path = semanticModel.GetConstantValue(arg as ExpressionSyntax).ToString();
                        }

                        if (!bo.drives.Contains(path))
                            bo.drives.Add(path);

                        continue;
                    }

                    if (_ma.AttributeClass.ToDisplayString() == "System.MCU.Compiler.Attributes.PackageConfigurationValueAttribute")
                    {
                        var name = na.FirstOrDefault(x => x.Key == "Name").Value.Value?.ToString();

                        if (name == default)
                        {
                            name = na.FirstOrDefault(x => x.Key == "NameArg").Value.Value?.ToString();

                            var arg = item.args[name];

                            name = semanticModel.GetConstantValue(arg as ExpressionSyntax).ToString();
                        }

                        if (!bo.packages.Contains(name))
                            bo.packages.Add(name);

                        continue;
                    }

                    if (_ma.AttributeClass.ToDisplayString() == "System.MCU.Compiler.Attributes.LibraryConfigurationValueAttribute")
                    {
                        var path = na.FirstOrDefault(x => x.Key == "Path").Value.Value?.ToString();

                        if (path == default)
                        {
                            path = na.FirstOrDefault(x => x.Key == "PathArg").Value.Value?.ToString();

                            var arg = item.args[path];

                            path = semanticModel.GetConstantValue(arg as ExpressionSyntax).ToString();
                        }

                        if (!bo.libraries.Contains(path))
                            bo.libraries.Add(path);

                        continue;
                    }

                    if (_ma.AttributeClass.ToDisplayString() == "System.MCU.Compiler.Attributes.DefineConfigurationValueAttribute")
                    {
                        var name = na.FirstOrDefault(x => x.Key == "Name").Value.Value?.ToString();

                        if (name == default)
                        {
                            name = na.FirstOrDefault(x => x.Key == "NameArg").Value.Value?.ToString();

                            var arg = item.args[name];

                            name = semanticModel.GetConstantValue(arg as ExpressionSyntax).ToString();
                        }

                        var value = na.FirstOrDefault(x => x.Key == "Value").Value.Value?.ToString();

                        if (value == default)
                        {
                            value = na.FirstOrDefault(x => x.Key == "ValueArg").Value.Value?.ToString();

                            var arg = item.args.GetValueOrDefault(value);

                            value = arg == null ? null : semanticModel.GetConstantValue(arg as ExpressionSyntax).ToString();
                        }

                        if (!bo.defines.ContainsKey(name))
                            bo.defines.Add(name, value);

                        continue;
                    }
                }
            }
        }

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
        bool IsTargetClass(INamedTypeSymbol? symbol, string targetBase)
        {
            while (symbol != null)
            {
                if (symbol.ToDisplayString() == targetBase) return true;
                symbol = symbol.BaseType;
            }
            return false;
        }

        #endregion
    }

    public record configureRecord(string fname, IMethodSymbol fsymb, Dictionary<string, object> args);

    public class CommonFileModel
    {
        public string Name { get; set; }
        public string State { get; set; }
        public DateTime PublishDate { get; set; }

        public CommonFileParametersModel Parameters { get; set; } = new();
    }
    public class CommonFileParametersModel
    {

        public string[] ProcessingPathes { get; set; } = Array.Empty<string>();
        public string[] RequiredValues { get; set; } = Array.Empty<string>();

        public Dictionary<string, string> DefaultValues { get; set; } = new();
    }
}
