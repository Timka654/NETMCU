using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Options;
using NETMCUCompiler.CodeBuilder;
using Newtonsoft.Json.Linq;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NETMCUCompiler
{
    public enum BuildingOutputType
    {
        Executable,
        Library
    }

    public class BuildingContext(string path, BuildingOutputType type)
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



        public async Task LoadAsync()
        {
            if (Options != null)
                throw new Exception("Building context is already loaded");

            await loadAsync(Options);
        }

        private async Task loadAsync(BuildingOptions? options)
        {
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

            await TryLoadProject();


            Options = new BuildingOptions();

            SemanticMethodExtractor e = new SemanticMethodExtractor();
            var configureMethods = e.ExtractInvocations(mcuConfigClassDeclaration, mcuConfigSemanticModel).ToArray();

            options ??= Options;

            foreach (var reference in referenceContexts)
            {
                await reference.Value.loadAsync(options);
                needsRebuildCore = needsRebuildCore || reference.Value.needsRebuildCore;
            }

            await LoadOptions(configureMethods, mcuConfigSemanticModel, options);

            await TryLoadCoreData(options);
        }

        CommonFileModel? commonData;

        bool needsRebuildCore;

        string mcuObjCorePath, mcuCorePath, tempDockerfilePath, dockerContent, commonCorePath, commonOldPath, buildDir, mcuBinPath;

        private async Task<bool> TryLoadCoreData(BuildingOptions bo)
        {
            var bc = this;

            if (!bo.Configurations.TryGetValue("CORE_PATH", out var corePath))
                throw new Exception("CORE_PATH is not defined in building options.");

            if (Directory.Exists(corePath))
                Console.WriteLine($"Core path exists: {corePath}");
            else
#if DEBUG
                corePath = "E:\\my_dev\\devmcu\\container\\core";
#else
                throw new Exception($"Core path does not exist: {corePath}");
#endif

            bo.Configurations["CFLAGS"] = string.Empty;

            bo.Configurations["EXECUTABLE_PROJECT_ROOT"] = RootPath;


            mcuCorePath = System.IO.Path.Combine(corePath, "mcu");

            StringBuilder dockerContentBuilder = new StringBuilder();

            var dockerFilePath = System.IO.Path.Combine(mcuCorePath, "Dockerfile.netmcu");

            if (File.Exists(dockerFilePath))
                dockerContentBuilder.AppendLine(File.ReadAllText(dockerFilePath));

            if (bo.Configurations.TryGetValue("MCU_TYPE", out var mcu_type))
            {
                mcuCorePath = System.IO.Path.Combine(mcuCorePath, mcu_type);

                dockerFilePath = System.IO.Path.Combine(mcuCorePath, "Dockerfile.netmcu");

                if (File.Exists(dockerFilePath))
                    dockerContentBuilder.AppendLine(File.ReadAllText(dockerFilePath));
                else
                    throw new Exception($"MCU specific Dockerfile not found: {dockerFilePath}");
            }
            else if (type == BuildingOutputType.Executable)
                throw new Exception("MCU_TYPE is not defined in building options.");

            if (!bo.Configurations.TryAdd("CFLAGS", string.Empty))
                bo.Configurations["CFLAGS"] = bo.Configurations["CFLAGS"].Trim();

            bo.Configurations["CFLAGS_DEFINES"] = string.Join(" ", bo.Defines.Select(d =>
            {
                var f = d.Key;
                if (!string.IsNullOrWhiteSpace(d.Value))
                    f += $"={d.Value}";
                return $"-D{f}";
            }));

            bo.Configurations["CFLAGS"] = $"{bo.Configurations["CFLAGS"].Trim()} {bo.Configurations["CFLAGS_DEFINES"]}";

            var libs = string.Join($" \\{Environment.NewLine}", bo.Libraries.GroupBy(x => x).Select(x => x.Key));

            if (!string.IsNullOrWhiteSpace(libs))
                libs += $" \\";

            bo.Configurations["CFLAGS_LIBS"] = libs;

            bo.Configurations["CFLAGS_INCLUDES"] = string.Join(" ", bo.Include.Select(inc => $"-I{inc}").GroupBy(x => x).Select(x => x.Key));

            bo.Configurations["CFLAGS"] = $"{bo.Configurations["CFLAGS"].Trim()} {bo.Configurations["CFLAGS_INCLUDES"]}";

            bo.Configurations["CFLAGS_MCU"] = bo.Configurations.TryGetValue("MCU", out var _mcu) ? $"-mcpu={_mcu}" : "";

            bo.Configurations["CFLAGS"] = $"{bo.Configurations["CFLAGS"].Trim()} {bo.Configurations["CFLAGS_MCU"]}";

            bo.Configurations["CFLAGS_OPTIMIZATION"] = bo.Configurations.TryGetValue("OPTIMIZATION", out var _optimization) ? $"-{_optimization}" : "-O2";

            bo.Configurations["CFLAGS"] = $"{bo.Configurations["CFLAGS"].Trim()} {bo.Configurations["CFLAGS_OPTIMIZATION"]}";

            bo.Configurations["CFLAGS_STARTUP_ADDRESS"] = bo.Configurations.TryGetValue("STARTUP_ADDRESS", out var _startup_address) ? $"-DUSER_CODE_ADDR={_startup_address}" : "";

            bo.Configurations["CFLAGS"] = $"{bo.Configurations["CFLAGS"].Trim()} {bo.Configurations["CFLAGS_STARTUP_ADDRESS"]}";

            bo.Configurations["CFLAGS"] = bo.Configurations["CFLAGS"].Trim();

            bo.Configurations["GIT_CLONE_COMMANDS"] = string.Join(Environment.NewLine, bo.GitRepositories.Select(repo =>
            {
                var url = repo.Url ?? throw new Exception("Repository URL is not defined.");
                var branch = repo.Branch ?? "main";
                return $"RUN git clone --branch {branch} --depth {(repo.Depth ?? 1)} {url} \"{repo.Path}\"";
            }));

            bo.Configurations["PACKAGE_INSTALL_COMMANDS"] = string.Join(Environment.NewLine, bo.Packages.Select(pkg => $"RUN apt-get install -y {pkg}"));

            dockerContent = dockerContentBuilder.ToString();



            mcuBinPath = System.IO.Path.Combine(this.BinPath, "NETMCU");

            if (!Directory.Exists(mcuBinPath))
                Directory.CreateDirectory(mcuBinPath);

            var objCorePath = System.IO.Path.Combine(bc.ObjPath, "NETMCU");

            if (!Directory.Exists(objCorePath))
                Directory.CreateDirectory(objCorePath);

            needsRebuildCore = false;

            if (type != BuildingOutputType.Executable) return true;


            bo.BuildConfigurations();

            commonCorePath = System.IO.Path.Combine(mcuCorePath, "common.netmcu");

            if (!File.Exists(commonCorePath))
                throw new Exception($"Common core file not found: {commonCorePath}");

            commonData = JsonSerializer.Deserialize<CommonFileModel>(File.ReadAllText(commonCorePath), JsonSerializerOptions.Web);

            foreach (var req in commonData.Parameters.RequiredValues)
            {
                if (!bo.Configurations.ContainsKey(req))
                    throw new Exception($"Required value '{req}' is not defined in building options.");
            }
            foreach (var def in commonData.Parameters.DefaultValues)
            {
                if (!bo.Configurations.ContainsKey(def.Key))
                    bo.Configurations[def.Key] = def.Value;
            }

            dockerContent = bo.FillConfiguration(dockerContent, out var ic, out var ir);

            foreach (var item in bo.InputConfigurations)
            {
                if (bo.Configurations.TryGetValue(item.Name, out var iVal))
                {
                    var validate = item.Type switch
                    {
                        "float" => double.TryParse(iVal, CultureInfo.InvariantCulture, out _),
                        "number" => long.TryParse(iVal, CultureInfo.InvariantCulture, out _),
                        "string" => true,
                        "bool" => bool.TryParse(iVal, out _),
                        _ => throw new Exception($"Unsupported input configuration type: {item.Type} for configuration '{item.Name}'")
                    };

                    if (!validate)
                    {
                        item.Messages.TryGetValue("INVALID_TYPE", out var msg);
                        throw new Exception(msg ?? $"Input configuration '{item.Name}' has invalid value format '{iVal}' for type '{item.Type}'.");
                    }

                    if (item.ValidValues != null && !item.ValidValues.Contains(iVal))
                    {
                        item.Messages.TryGetValue("INVALID_VALUE", out var msg);
                        throw new Exception(msg ?? $"Input configuration '{item.Name}' has invalid value '{iVal}'. Valid values are: {string.Join(", ", item.ValidValues.Select(x => $"\"{x}\""))}.");
                    }
                }
                else
                {
                    if (item.Required)
                    {
                        item.Messages.TryGetValue("REQUIRED_VALUE", out var msg);
                        throw new Exception(msg ?? $"Required input configuration '{item.Name}' is not defined in building options.");
                    }

                    if (item.DefaultValue != default)
                        bo.Configurations[item.Name] = item.DefaultValue;
                }
            }


            mcuObjCorePath = System.IO.Path.Combine(objCorePath, "mcu_core", mcu_type);

            if (!Directory.Exists(mcuObjCorePath))
                Directory.CreateDirectory(mcuObjCorePath);

            // Генерируем временный Dockerfile
            tempDockerfilePath = System.IO.Path.Combine(mcuObjCorePath, "Dockerfile");

            string oldDockerfileContent = "";

            if (File.Exists(tempDockerfilePath))
            {
                oldDockerfileContent = File.ReadAllText(tempDockerfilePath);
                needsRebuildCore = dockerContent != oldDockerfileContent;
            }
            else
                needsRebuildCore = true;

            commonOldPath = System.IO.Path.Combine(mcuObjCorePath, "common.netmcu");

            string commonOldContent = "";

            if (File.Exists(commonOldPath))
            {
                commonOldContent = File.ReadAllText(commonOldPath);

                needsRebuildCore = needsRebuildCore || (File.ReadAllText(commonOldPath) != File.ReadAllText(commonCorePath));
            }
            else
                needsRebuildCore = true;

            buildDir = System.IO.Path.Combine(mcuObjCorePath, "build");

            needsRebuildCore = needsRebuildCore || !Directory.Exists(buildDir);

            return true;
        }

        public async Task<bool> BuildCore()
        {
            var bo = Options;

            if (needsRebuildCore)
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
                            if (bo.Configurations.TryGetValue(key, out var value))
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

                var pathes = new List<string>() {
                $"-v \"{mcuObjCorePath}:/project\""
                };

                pathes.AddRange(bo.Drives.Select(d => $"-v \"{d.Path}:{d.ContainerPath}\""));

                var coreBuildCmd = $"run --rm {string.Join(" ", pathes)} final-mcu";
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

        CompilationContext? compilationContext;
        //LinkerContext? compilationLinker = new LinkerContext();

        public async Task<bool> Compile()
        {
            return await compile(/*compilationLinker*/);
        }

        private async Task<bool> compile(/*LinkerContext? linker*/)
        {
        //    var buildLinkers = new List<LinkerContext>() { compilationLinker };

        //    if (linker != null && linker != compilationLinker)
        //        buildLinkers.Add(linker);

            compilationContext = new CompilationContext()
            {
                ParentContext = null,
                ExceptTypes = [mcuConfigClassDeclaration],
                ExceptMethods = [],
                BinaryPath = System.IO.Path.Combine(mcuBinPath, "output.bin"),
                //LinkerContexts = buildLinkers.ToArray(),
                BuildingContext = this,
                MainMethod = ProgramMainNode,
                ProgramClass = ProgramMainTypeNode,
                SemanticModel = null
            };

            //if (type == BuildingOutputType.Executable)
            //{
            //    Stm32MethodBuilder e = new Stm32MethodBuilder(compilationContext, ProgramSemanticModel);

            //    e.BuildAsm(ProgramMainNode);
            //}

            foreach (var reference in referenceContexts)
            {
                if (!await reference.Value.compile(/*linker*/))
                    throw new Exception($"Failed to compile referenced project: {reference.Value.Path}");
                compilationContext.FillConstants(reference.Value.compilationContext!.GetConstantMap());
            }


            //foreach (var tree in Compilation.SyntaxTrees)
            //{
            //    var sm = Compilation.GetSemanticModel(tree);

            LibraryCompiler.CompileProject(Compilation, compilationContext);
            //}



            DumpMeta();

            if (type == BuildingOutputType.Executable)
                DumpApplication();
            else
                DumpLibrary();

            DumpLinker();

            return true;
        }

        IEnumerable<BuildingContext> CollectReferences()
        {
            foreach (var item in referenceContexts.Select(x => x.Value))
            {
                yield return item;
            }

            foreach (var item in referenceContexts.SelectMany(x => x.Value.CollectReferences()))
            {
                yield return item;
            }

        }

        IEnumerable<MethodCompilationContext> Methods => compilationContext.Childs
                .SelectMany(x => x.Value.Childs.Values)
                .OfType<MethodCompilationContext>();

        IEnumerable<MethodCompilationContext> PublicMethods => Methods.Where(m => m.IsPublic);


        byte[] BuildAppImage()
        {
            // Список для хранения финального бинарника

            using var finalImage = new MemoryStream();
            // Карта смещений: Контекст -> Смещение в итоговом файле
            Dictionary<CompilationContext, int> contextOffsets = new();



            Dictionary<string, uint> globalSymbolTable = new();

            // 1. Добавляем символы ядра (из kernel.map)
            foreach (var coreSym in CoreSymbols)
                if (!globalSymbolTable.TryAdd(coreSym.Key, (uint)coreSym.Value)) throw new Exception($"Duplicate symbol in global symbol table: {coreSym.Key}");


            using (var kernel = System.IO.File.OpenRead(System.IO.Path.Combine(buildDir, "kernel.elf")))
            { 
                kernel.CopyTo(finalImage);
            }


            uint userCodeAddr = 0;
            var sa = Options.Configurations["STARTUP_ADDRESS"];

            if (sa.StartsWith("0x"))
                userCodeAddr = uint.Parse(sa.TrimStart("0x"), System.Globalization.NumberStyles.HexNumber);
            else
                userCodeAddr = uint.Parse(sa);

            finalImage.Seek(userCodeAddr, SeekOrigin.Current);


            Dictionary<string, List<int>> coreCallOffsets = new ();

            var refs = CollectReferences().Prepend(this).ToArray();

            foreach (var ctx in refs)
            {
                int startOffset = (int)finalImage.Position;

                foreach (var item in ctx.PublicMethods)
                {
                    if (!globalSymbolTable.TryAdd(item.Name,  (uint)(startOffset + item.BinaryOffset))) throw new Exception($"Duplicate symbol in global symbol table: {item.Name}");
                }

                foreach (var item in ctx.compilationContext.NativeRelocations)
                {
                    if (!coreCallOffsets.TryGetValue(item.Key, out var redirections))
                    {
                        redirections = new List<int>();
                        coreCallOffsets[item.Key] = redirections;
                    }
                    
                    redirections.AddRange(item.Value.Select(x=>(int)(x.Context.BinaryOffset + x.Offset + startOffset)));
                }

                finalImage.Write(ctx.BuildImage());

                contextOffsets[ctx.compilationContext!] = startOffset;
            }

            byte[] binary = finalImage.ToArray();


            // 2. Добавляем методы наших библиотек
            //foreach (var method in globalSymbolTable)
            //{
            //    int libOffset = contextOffsets[method.Value.context.Class.Global];
            //    uint absoluteAddr = (uint)(userCodeAddr + libOffset + method.Value.position);
            //    globalSymbolTable[method.Key] = absoluteAddr;
            //}

            // Проходим по всем релокациям (импортам методов)
            foreach (var inputGroup in coreCallOffsets)
            {
                string methodName = inputGroup.Key;

                // Ищем адрес цели в глобальной таблице символов
                if (!globalSymbolTable.TryGetValue(methodName, out uint targetAddr))
                    throw new Exception($"Undefined reference: {methodName}");

                foreach (var rel in inputGroup.Value)
                {
                    // 1. Находим, где в итоговом массиве лежит инструкция BL
                    int instrGlobalOffset = rel;
                    uint instrAddr = (uint)(userCodeAddr + instrGlobalOffset);

                    // 2. Считаем дистанцию (Target - (PC + 4))
                    int jumpOffset = (int)(targetAddr - (instrAddr + 4));

                    // 3. ПАТЧИМ: передаем массив, позицию и дистанцию
                    // binary — это твой finalImage.ToArray()
                    ASMInstructions.PatchThumb2BL(binary, instrGlobalOffset, jumpOffset);
                }
            }

            return binary;
        }


        ClassDeclarationSyntax? mcuConfigClassDeclaration = null;
        SemanticModel? mcuConfigSemanticModel = null;

        private async Task TryLoadProject()
        {
            using (var workspace = MSBuildWorkspace.Create())
            {
                // 2. Загружаем проект напрямую
                var project = await workspace.OpenProjectAsync(Path);

                CollectProjectContexts(project);

                var msbuildProject = new Microsoft.Build.Evaluation.Project(project.FilePath);

                string outDir = msbuildProject.GetPropertyValue("OutDir"); // Путь к bin
                string intermediateDir = msbuildProject.GetPropertyValue("IntermediateOutputPath"); // Путь к obj
                RootPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(project.FilePath, ".."));
                ObjPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(RootPath, intermediateDir));
                BinPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(RootPath, outDir));

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

                        if (type == BuildingOutputType.Executable)
                        {
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
                                        ProgramMainTypeNode = classDecl;
                                        ProgramSemanticModel = semanticModel;
                                    }
                                }
                            }
                        }

                        // Проверяем наследование от вашего базового класса в NETMCUCore
                        if (IsTargetClass(symbol, "System.MCU.Compiler.ConfigureEntry"))
                        {
                            if (mcuConfigClassDeclaration != null)
                                throw new Exception("Multiple configuration classes found in project. Only one configuration class is allowed.");

                            mcuConfigClassDeclaration = classDecl;
                            mcuConfigSemanticModel = semanticModel;
                            Console.WriteLine($"Найдена конфигурация: {symbol.Name} в файле {tree.FilePath}");
                            // Здесь вызывайте вашу логику извлечения аргументов
                        }
                    }
                }


                if (mcuConfigClassDeclaration == null)
                    throw new Exception("No configuration class found in project. You must have single class intherits from System.MCU.Compiler.ConfigureEntry");


            }
        }


        Dictionary<ProjectId, BuildingContext> referenceContexts = new Dictionary<ProjectId, BuildingContext>();

        void CollectProjectContexts(Microsoft.CodeAnalysis.Project project)
        {
            var solution = project.Solution;

            foreach (var reference in project.ProjectReferences)
            {
                // Находим проект в текущем решении по его ID
                var referencedProject = solution.GetProject(reference.ProjectId);

                if (referencedProject != null && !string.IsNullOrEmpty(referencedProject.FilePath) && referencedProject.Name != "NETMCUCore")
                {
                    // Добавляем путь к .csproj файлу
                    referenceContexts[reference.ProjectId] = new BuildingContext(referencedProject.FilePath, BuildingOutputType.Library);
                }
            }
        }


        private async Task LoadOptions(configureRecord[] configureMethods, SemanticModel semanticModel, BuildingOptions bo)
        {
            TValue? GetBaseValue<TValue>(AttributeData _ma
                , configureRecord item
                , string name
                , string argName
                , out bool exists
                , bool optionalArg = false)
                where TValue : IConvertible
            {
                var na = _ma.NamedArguments;

                name = na.FirstOrDefault(x => x.Key == name).Value.Value?.ToString();

                if (name == default)
                {
                    name = na.FirstOrDefault(x => x.Key == argName).Value.Value?.ToString();

                    item.args.TryGetValue(name, out var arg);

                    if (optionalArg && arg == null)
                    {
                        exists = false; return default(TValue?);
                    }

                    if (arg is ExpressionSyntax argExp)
                    {
                        name = semanticModel.GetConstantValue(arg as ExpressionSyntax).ToString();
                        exists = true;
                    }
                    else
                        throw new InvalidCastException($"Argument '{argName}' value is not an expression syntax. Actual type: {arg.GetType().FullName}");
                }
                else
                    exists = true;


                return (TValue)Convert.ChangeType(name, typeof(TValue));
            }

            TValue[]? GetArrayBaseValue<TValue>(AttributeData _ma
                , configureRecord item
                , string name
                , string argName
                , out bool exists
                , bool optionalArg = false)
                where TValue : IConvertible
            {
                var na = _ma.NamedArguments;

                List<TValue>? values = null;

                values = na
                    .Where(x => x.Key == name)
                    .Select(x => (KeyValuePair<string, TypedConstant>?)x)
                    .FirstOrDefault()?
                    .Value
                    .Values
                    .Select(x => (TValue)Convert.ChangeType(x.Value.ToString(), typeof(TValue)))
                    .ToList();

                if (values == default)
                {
                    name = na.FirstOrDefault(x => x.Key == argName).Value.Value?.ToString();

                    item.args.TryGetValue(name, out var arg);

                    if (optionalArg && arg == null)
                    {
                        exists = false; return default(TValue[]?);
                    }

                    if (arg is CollectionExpressionSyntax collectionExp)
                    {

                        foreach (var element in collectionExp.Elements)
                        {
                            if (element is ExpressionElementSyntax exprElement)
                            {
                                var constantValue = semanticModel.GetConstantValue(exprElement.Expression);
                                if (constantValue.HasValue && constantValue.Value != null)
                                {
                                    values.Add((TValue)Convert.ChangeType(constantValue.Value.ToString(), typeof(TValue)));
                                }
                            }
                        }
                        exists = true;
                        return values.ToArray();
                    }
                    else
                        throw new InvalidCastException($"Argument '{argName}' value is not an expression syntax. Actual type: {arg.GetType().FullName}");
                }
                else
                    exists = true;

                return values.ToArray();
            }

            string[][]? GetArrayValue(AttributeData _ma
                , configureRecord item
                , string argName
                , bool optionalArg = false)
            {
                var na = _ma.NamedArguments;

                List<string[]>? values = null;

                var name = na.FirstOrDefault(x => x.Key == argName).Value.Value?.ToString();

                item.args.TryGetValue(name, out var arg);

                if (optionalArg && arg == null) return default(string[][]?);

                if (arg is CollectionExpressionSyntax collectionExp)
                {
                    values = new List<string[]>();
                    foreach (var element in collectionExp.Elements)
                    {
                        if (element is ExpressionElementSyntax exprElement)
                        {
                            if (exprElement.Expression is ObjectCreationExpressionSyntax objCreation)
                            {
                                var args = objCreation.ArgumentList?.Arguments;
                                if (args != null)
                                {
                                    // Превращаем аргументы конструктора (Enum и String) в массив строк
                                    var errorData = args.Value.Select(a =>
                                    {
                                        var val = semanticModel.GetConstantValue(a.Expression).Value;
                                        return val?.ToString() ?? "";
                                    }).ToArray();

                                    values.Add(errorData);
                                }
                            }
                        }
                    }

                    return values.ToArray();
                }
                else
                    throw new InvalidCastException($"Argument '{argName}' value is not an expression syntax. Actual type: {arg.GetType().FullName}");
            }


            foreach (var item in configureMethods)
            {
                var ma = item.fsymb.GetAttributes();

                foreach (var _ma in ma)
                {
                    if (_ma.AttributeClass.BaseType?.ToDisplayString() != "System.MCU.Compiler.Attributes.MCUConfigurationValueAttribute")
                        continue;

                    if (_ma.AttributeClass.ToDisplayString() == "System.MCU.Compiler.Attributes.InputConfigurationValueAttribute")
                    {
                        var name = GetBaseValue<string>(_ma, item, "Name", "NameArg", out _);
                        var type = GetBaseValue<string>(_ma, item, "Type", "TypeArg", out _);

                        var defaultValue = GetBaseValue<string>(_ma, item, "DefaultValue", "DefaultValueArg", out _, true);

                        var required = GetBaseValue<bool>(_ma, item, "Required", "RequiredArg", out var reqEx, true);

                        if (!reqEx) required = true;

                        var validValues = GetArrayBaseValue<string>(_ma, item, "ValidValues", "ValidValuesArg", out _, true);

                        var messages = GetArrayValue(_ma, item, "ErrorsArg", true);

                        //var validValues = na.FirstOrDefault(x => x.Key == "Required").Value.Value?.ToString();

                        //if (validValues == default)
                        //{
                        //    validValues = na.FirstOrDefault(x => x.Key == "RequiredArg").Value.Value?.ToString();

                        //    var arg = item.args[validValues];

                        //    validValues = semanticModel.GetConstantValue(arg as ExpressionSyntax).ToString();
                        //}
                        bo.InputConfigurations.Add(new BuildingInputConfigurationModel
                        {
                            Name = name,
                            Type = type,
                            DefaultValue = defaultValue,
                            Required = required,
                            ValidValues = validValues,
                            Messages = messages?.ToDictionary(x => x[0], x => x[1])
                        });

                        continue;
                    }
                     
                    if (_ma.AttributeClass.ToDisplayString() == "System.MCU.Compiler.Attributes.RepositoryConfigurationValueAttribute")
                    {
                        var path = GetBaseValue<string>(_ma, item, "Path", "PathArg", out _);

                        var url = GetBaseValue<string>(_ma, item, "Url", "UrlArg", out _);

                        var branch = GetBaseValue<string>(_ma, item, "Branch", "BranchArg", out _);

                        var depth = GetBaseValue<int>(_ma, item, "Depth", "DepthArg", out var depthEx);

                        if (!depthEx) depth = 1;

                        bo.GitRepositories.Add(new GitRepositoryConfiguration
                        {
                            Path = path,
                            Url = url,
                            Branch = branch,
                            Depth = depth
                        });

                        continue;
                    }

                    if (_ma.AttributeClass.ToDisplayString() == "System.MCU.Compiler.Attributes.ReplaceConfigurationValueAttribute")
                    {
                        var name = GetBaseValue<string>(_ma, item, "Name", "NameArg", out _);

                        var value = GetBaseValue<string>(_ma, item, "Value", "ValueArg", out _);

                        bo.Configurations[name] = value;

                        continue;
                    }

                    if (_ma.AttributeClass.ToDisplayString() == "System.MCU.Compiler.Attributes.IncludeConfigurationValueAttribute")
                    {
                        var include = GetBaseValue<string>(_ma, item, "Include", "IncludeArg", out _);

                        if (!bo.Include.Contains(include))
                            bo.Include.Add(include);

                        continue;
                    }

                    if (_ma.AttributeClass.ToDisplayString() == "System.MCU.Compiler.Attributes.DriveConfigurationValueAttribute")
                    {
                        var path = GetBaseValue<string>(_ma, item, "Path", "PathArg", out _);
                        var containerPath = GetBaseValue<string>(_ma, item, "ContainerPath", "ContainerPathArg", out _);

                        if (!bo.Drives.Any(d => d.Path == path && d.ContainerPath == containerPath))
                            bo.Drives.Add(new DriveConfiguration { Path = path, ContainerPath = containerPath });

                        continue;
                    }

                    if (_ma.AttributeClass.ToDisplayString() == "System.MCU.Compiler.Attributes.PackageConfigurationValueAttribute")
                    {
                        var name = GetBaseValue<string>(_ma, item, "Name", "NameArg", out _);

                        if (!bo.Packages.Contains(name))
                            bo.Packages.Add(name);

                        continue;
                    }

                    if (_ma.AttributeClass.ToDisplayString() == "System.MCU.Compiler.Attributes.LibraryConfigurationValueAttribute")
                    {
                        var path = GetBaseValue<string>(_ma, item, "Path", "PathArg", out _);

                        if (!bo.Libraries.Contains(path))
                            bo.Libraries.Add(path);

                        continue;
                    }

                    if (_ma.AttributeClass.ToDisplayString() == "System.MCU.Compiler.Attributes.DefineConfigurationValueAttribute")
                    {
                        var name = GetBaseValue<string>(_ma, item, "Name", "NameArg", out _);

                        var value = GetBaseValue<string>(_ma, item, "Value", "ValueArg", out _, true);

                        if (!bo.Defines.ContainsKey(name))
                            bo.Defines.Add(name, value);

                        continue;
                    }
                }
            }
        }


        void DumpMeta()
        {

            var outputPath = System.IO.Path.Combine(mcuBinPath, "meta.netmcu");

            var meta = new
            {
                CompilerOptions = new
                {
                    Options.Include,
                    Options.Libraries,
                    Options.Defines,
                    Options.Packages,
                    Options.InputConfigurations
                }
            };

            string json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outputPath, json);
        }

        void DumpLinker()
        {
            var outputPath = System.IO.Path.Combine(mcuBinPath, "linker.netmcu");

            var methods = Methods;

            if(methods.Any(x=>x.BinaryOffset == null))
                throw new Exception("Some methods have null BinaryOffset. First you must build image.");

            var meta = new
            {
                OutputMethods = methods
                .Where(x=>x.IsPublic)
                .ToDictionary(x => x.Name, x => new { x.BinaryOffset, x.IsStatic }),
                OutputTypes = compilationContext.Childs
                .OfType<TypeCompilationContext>()
                .ToDictionary(
                    x => x.Name,
                    x => new { x.Size, x.IsClass, x.FieldOffsets }
                    ),
                InputMethods = compilationContext.NativeRelocations.ToDictionary(x => x.Key, x => x.Value.Select(x => x.Offset))
            };
            string json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outputPath, json);
        }

        void DumpLibrary()
            => DumpBinary(BuildImage());
        void DumpApplication()
            => DumpBinary(BuildAppImage());

        byte[]? builtImage = null;
        byte[] BuildImage()
        {
            if (builtImage != null) return builtImage;

            using MemoryStream methodData = new MemoryStream();

            foreach (var type in compilationContext.Childs)
            {
                foreach (var method in type.Value.Childs.Values.Cast<MethodCompilationContext>())
                {
                    method.BinaryOffset = (int)methodData.Position;

                    var oldPos = method.Bin.Position;

                    method.Bin.Position = 0;
                    
                    method.Bin.CopyTo(methodData);
                    
                    method.Bin.Position = oldPos;
                }
            }

            builtImage = methodData.ToArray();

            return builtImage;
        }

        string BuildAsm()
        {
            StringBuilder sb = new StringBuilder();
            foreach (var type in compilationContext.Childs)
            {
                foreach (var method in type.Value.Childs.Values.Cast<MethodCompilationContext>())
                {
                    sb.AppendLine($"; Method: {method.Name}, Offset: 0x{method.BinaryOffset:X}");
                    sb.Append(method.Asm);
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        void DumpBinary(byte[] data)
        {
            var outputPath = compilationContext.BinaryPath;
#if DEBUG
            var asmPath = string.Join('.', outputPath.Split('.').SkipLast(1)) + ".asm";

            File.WriteAllText(asmPath, BuildAsm());
#endif
            File.WriteAllBytes(outputPath, data);
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
    public record RelocationRecord(MethodCompilationContext Context, int Offset, bool isStatic);
    //public record LinkerRecord(MethodCompilationContext context, int position, bool isStatic);

    //public class LinkerContext
    //{
    //    public Dictionary<string, LinkerRecord> OutputMethods { get; } = new();

    //    public Dictionary<string, TypeMetadata> OutputTypes { get; } = new();

    //    public Dictionary<string, List<RelocationRecord>> InputMethods { get; } = new();

    //    public Dictionary<string, object> Constants { get; } = new();
    //}
}
