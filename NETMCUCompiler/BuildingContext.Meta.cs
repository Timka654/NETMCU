using Microsoft.Build.Evaluation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using NETMCUCompiler.Shared.Compilation.Building;
using System;
using System.Collections.Generic;
using System.Text;

namespace NETMCUCompiler
{
    public partial class SolutionContext
    {
        public BuildingContext StartupProject { get; set; }

        public Dictionary<string, BuildingContext> Projects { get; } = new();
    }

    //meta
    public partial class BuildingContext
    {

        async Task CollectProjectContexts(Microsoft.CodeAnalysis.Project project, BuildingOptions options)
        {
            var solution = project.Solution;

            foreach (var reference in project.ProjectReferences)
            {
                // Находим проект в текущем решении по его ID
                var referencedProject = solution.GetProject(reference.ProjectId);

                if (referencedProject != null && !string.IsNullOrEmpty(referencedProject.FilePath)/* && referencedProject.Name != "NETMCUCore"*/)
                {
                    if (!solutionContext.Projects.TryGetValue(referencedProject.FilePath, out var existingContext))
                    {
                        existingContext = new BuildingContext(referencedProject.FilePath, BuildingOutputTypeEnum.Library, solutionContext);

                        solutionContext.Projects[referencedProject.FilePath] = existingContext;

                        await existingContext.loadAsync(options);
                    }

                    // Добавляем путь к .csproj файлу
                    referenceContexts[reference.ProjectId] = existingContext;
                }
            }
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

            Options = new BuildingOptions();

            options ??= Options;

            if (!await TryLoadProject(options))
                throw new Exception($"Project context already have in solution context");

            SemanticMethodExtractor e = new SemanticMethodExtractor();
            var configureMethods = e.ExtractInvocations(mcuConfigClassDeclaration, mcuConfigSemanticModel).ToArray();

            foreach (var reference in referenceContexts)
            {
                //if (reference.Value.Options == null)
                //    await reference.Value.loadAsync(options);
                needsRebuildCore = needsRebuildCore || reference.Value.needsRebuildCore;
            }

            await LoadOptions(configureMethods, mcuConfigSemanticModel, options);

            await TryLoadCoreData(options);
        }

        private async Task<bool> TryLoadProject(BuildingOptions options)
        {
            using (var workspace = MSBuildWorkspace.Create())
            {
                // 2. Загружаем проект напрямую
                var project = await workspace.OpenProjectAsync(Path);

                await CollectProjectContexts(project, options);

                var msbuildProject = new Microsoft.Build.Evaluation.Project(project.FilePath);

                string typeMetaDataLevel = msbuildProject.GetPropertyValue("TypeMetaDataLevel");
                if (!string.IsNullOrEmpty(typeMetaDataLevel))
                {
                    options.TypeMetaDataLevel = typeMetaDataLevel;
                }

                string typeHeaderStr = msbuildProject.GetPropertyValue("TypeHeader");
                if (!string.IsNullOrEmpty(typeHeaderStr) && bool.TryParse(typeHeaderStr, out bool typeHeader))
                {
                    options.TypeHeader = typeHeader;
                }

                string outDir = msbuildProject.GetPropertyValue("OutDir"); // Путь к bin
                string intermediateDir = msbuildProject.GetPropertyValue("IntermediateOutputPath"); // Путь к obj
                RootPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(project.FilePath, ".."));
                ObjPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(RootPath, intermediateDir));
                BinPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(RootPath, outDir));

                ProjectCollection.GlobalProjectCollection.UnloadProject(msbuildProject);

                var compilation = await project.GetCompilationAsync();

                if (compilation == null) return true;

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

                        if (type == BuildingOutputTypeEnum.Executable)
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

            return true;
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
    }
}
