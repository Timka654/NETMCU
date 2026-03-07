using NETMCUCompiler.CodeBuilder;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace NETMCUCompiler
{
    public partial class BuildingContext
    {

        byte[]? builtImage = null;


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
            Console.WriteLine($"DumpLinker {Path} - Compiled = {!methods.Any(x => x.BinaryOffset == null)}");

            if (methods.Any(x => x.BinaryOffset == null && x.NativeName == null))
                throw new Exception("Some methods have null BinaryOffset. First you must build image.");

            var meta = new
            {
                OutputMethods = methods
                .Where(x => x.IsPublic)
                .ToDictionary(x => x.Name, x => new { x.BinaryOffset, x.IsStatic, x.NativeName }),
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
        {
            DumpBinary(BuildImage());

            var nativeDir = System.IO.Path.Combine(mcuBinPath, "native");
            if (!Directory.Exists(nativeDir))
                Directory.CreateDirectory(nativeDir);

            var extensions = new[] { ".c", ".cpp", ".cc", ".h", ".hpp", ".s", ".ld" };
            foreach (var ext in extensions)
            {
                var files = Directory.GetFiles(RootPath, $"*{ext}", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (file.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}") ||
                        file.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}"))
                        continue;

                    var relativePath = System.IO.Path.GetRelativePath(RootPath, file);
                    var destFile = System.IO.Path.Combine(nativeDir, relativePath);
                    var destDir = System.IO.Path.GetDirectoryName(destFile);
                    if (!Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir!);

                    File.Copy(file, destFile, true);
                }
            }
        }
        void DumpApplication()
            => DumpBinary(BuildAppImage());

        byte[] BuildImage()
        {
            if (builtImage != null) return builtImage;

            using MemoryStream methodData = new MemoryStream();

            void WriteMethods(IEnumerable<BaseCompilationContext> methods)
            {
                foreach (var method in methods.OfType<MethodCompilationContext>())
                {
                    //if (method.NativeName != null) continue;

                    method.BinaryOffset = (int)methodData.Position;

                    var oldPos = method.Bin.Position;

                    method.Bin.Position = 0;

                    method.Bin.CopyTo(methodData);

                    method.Bin.Position = oldPos;

                    WriteMethods(method.Childs.Values.OfType<MethodCompilationContext>());
                }
            }


            foreach (var type in compilationContext.Childs)
            {
                WriteMethods(type.Value.Childs.Values.OfType<MethodCompilationContext>());
            }

            builtImage = methodData.ToArray();

            foreach (var item in referenceContexts.Values)
            {
                item.BuildImage();
            }

            var refMethods = referenceContexts
                .SelectMany(x => x.Value.PublicMethods)
                .Concat(Methods.Where(x => !x.IsPublic))
                .ToDictionary(x => x.Name, x => x);

            var relocs = compilationContext.ReferenceRelocations;

            foreach (var item in relocs.ToArray())
            {
                if (!refMethods.TryGetValue(item.Key, out var method))
                    throw new Exception($"Ref methods does not contains {item.Key}");

                foreach (var x in item.Value)
                {
                    ASMInstructions.PatchThumb2BL(builtImage, (int)(x.Context.BinaryOffset! + x.Offset), method.BinaryOffset!.Value);

                }
            }

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
            var asmPath = string.Join('.', outputPath.Split('.').SkipLast(1)) + ".asm";

            File.WriteAllText(asmPath, BuildAsm());
            File.WriteAllBytes(outputPath, data);
        }

        public async Task<bool> Compile()
        {
            return await compile(/*compilationLinker*/);
        }

        private async Task<bool> compile(/*LinkerContext? linker*/)
        {
            if (compilationContext != null)
            {
                Console.WriteLine($"Compile {Path} - already compiled. Skipping");

                return true;
            }

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

            foreach (var reference in referenceContexts)
            {
                if (!await reference.Value.compile(/*linker*/))
                    throw new Exception($"Failed to compile referenced project: {reference.Value.Path}");
                compilationContext.FillConstants(reference.Value.compilationContext!.GetConstantMap());
            }

            Console.WriteLine($"Compile {Path} ...");

            LibraryCompiler.CompileProject(Compilation, compilationContext);

            DumpMeta();

            if (type == BuildingOutputTypeEnum.Executable)
                DumpApplication();
            else
                DumpLibrary();

            DumpLinker();

            return true;
        }


        private byte[] BuildRoDataSection(out Dictionary<string, uint> offsets)
        {
            offsets = new Dictionary<string, uint>();
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            // Используем StringLiterals из главного контекста компиляции
            foreach (var literal in compilationContext.StringLiterals.OrderBy(l => l.Key)) // Сортируем для стабильной сборки
            {
                // Выравниваем каждую строку по 4-байтной границе, стандарт для ARM
                while (ms.Position % 4 != 0)
                {
                    writer.Write((byte)0);
                }

                // Сохраняем смещение для этого символа
                offsets[literal.Key] = (uint)ms.Position;

                // Записываем строку в кодировке UTF-8 с нулевым терминатором
                var bytes = Encoding.UTF8.GetBytes(literal.Value);
                writer.Write(bytes);
                writer.Write((byte)0); // Нулевой терминатор
            }

            return ms.ToArray();
        }

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


            using (var kernel = System.IO.File.OpenRead(System.IO.Path.Combine(buildDir, "kernel.bin")))
            {
                kernel.CopyTo(finalImage);
            }


            uint userCodeAddr = 0;
            var sa = Options.Configurations["STARTUP_ADDRESS"];

            if (sa.StartsWith("0x"))
                userCodeAddr = uint.Parse(sa.TrimStart("0x"), System.Globalization.NumberStyles.HexNumber);
            else
                userCodeAddr = uint.Parse(sa);

            var flashBaseAddrStr = Options.Configurations["FLASH_BASE_ADDRESS"];

            uint flashBaseAddress = flashBaseAddrStr.StartsWith("0x")
                ? uint.Parse(flashBaseAddrStr.TrimStart("0x"), System.Globalization.NumberStyles.HexNumber)
                : uint.Parse(flashBaseAddrStr);


            // Проверяем, что ядро не налезает на пользовательский код
            long kernelEndAddr = flashBaseAddress + finalImage.Position;
            if (kernelEndAddr > userCodeAddr)
            {
                throw new Exception($"Kernel is too large (ends at 0x{kernelEndAddr:X}) and overlaps with user code address (0x{userCodeAddr:X}).");
            }

            // Устанавливаем позицию для записи пользовательского кода
            finalImage.Position = userCodeAddr - flashBaseAddress;


            Dictionary<string, List<int>> coreCallOffsets = new();
            Dictionary<string, List<int>> refCallOffsets = new();
            Dictionary<string, List<int>> dataRelocOffsets = new();

            //var refs = CollectReferences().Prepend(this).ToArray();

            foreach (var ctx in solutionContext.Projects.Values)
            {
                int startOffset = (int)finalImage.Position;
                uint libraryBaseAddr = flashBaseAddress + (uint)startOffset;

                finalImage.Write(ctx.BuildImage());

                foreach (var item in ctx.compilationContext.DataRelocations)
                {
                    if (!dataRelocOffsets.TryGetValue(item.Key, out var redirections))
                    {
                        redirections = new List<int>();
                        dataRelocOffsets[item.Key] = redirections;
                    }
                    redirections.AddRange(item.Value.Select(x => (int)(x.Context.BinaryOffset + x.Offset + startOffset)));
                }

                foreach (var item in ctx.PublicMethods)
                {
                    if (!globalSymbolTable.TryAdd(item.Name, (uint)(libraryBaseAddr + item.BinaryOffset))) throw new Exception($"Duplicate symbol in global symbol table: {item.Name}");
                }

                foreach (var item in ctx.compilationContext.NativeRelocations)
                {
                    if (!coreCallOffsets.TryGetValue(item.Key, out var redirections))
                    {
                        redirections = new List<int>();
                        coreCallOffsets[item.Key] = redirections;
                    }

                    redirections.AddRange(item.Value.Select(x => (int)(x.Context.BinaryOffset + x.Offset + startOffset)));
                }



                contextOffsets[ctx.compilationContext!] = startOffset;
            }
            // --- СОЗДАНИЕ И ДОБАВЛЕНИЕ СЕКЦИИ .RODATA ---

            // 1. Создаем бинарный блок с данными и получаем смещения внутри него
            var roDataBytes = BuildRoDataSection(out var roDataOffsets);

            // 2. Выравниваем текущую позицию в прошивке
            while (finalImage.Position % 4 != 0)
            {
                finalImage.WriteByte(0);
            }

            // 3. Запоминаем абсолютный адрес начала секции .rodata
            uint roDataSectionAddr = flashBaseAddress + (uint)finalImage.Position;

            // 4. Записываем саму секцию в прошивку
            finalImage.Write(roDataBytes, 0, roDataBytes.Length);

            // 5. Добавляем символы строк в глобальную таблицу символов
            foreach (var entry in roDataOffsets)
            {
                var symbolName = entry.Key;
                var offsetInSection = entry.Value;
                var absoluteAddr = roDataSectionAddr + offsetInSection;
                if (!globalSymbolTable.TryAdd(symbolName, absoluteAddr))
                {
                    throw new Exception($"Duplicate symbol in global symbol table: {symbolName}");
                }
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

                foreach (var instrGlobalOffset in inputGroup.Value)
                {
                    // 1. Вычисляем АБСОЛЮТНЫЙ адрес инструкции BL
                    uint instrAddr = flashBaseAddress + (uint)instrGlobalOffset;

                    // 2. Считаем дистанцию (Target - (PC + 4))
                    int jumpOffset = (int)(targetAddr - (instrAddr + 4));

                    // 3. ПАТЧИМ: передаем массив, позицию и дистанцию
                    // binary — это твой finalImage.ToArray()
                    ASMInstructions.PatchThumb2BL(binary, instrGlobalOffset, jumpOffset);
                }
            }

            foreach (var inputGroup in dataRelocOffsets)
            {
                string symbolName = inputGroup.Key;

                if (!globalSymbolTable.TryGetValue(symbolName, out uint targetAddr))
                    throw new Exception($"Undefined data reference: {symbolName}");

                foreach (var instrGlobalOffset in inputGroup.Value)
                {
                    // Здесь вам нужно будет вызвать новую функцию-патчер,
                    // которая запишет 32-битный адрес `targetAddr`
                    // в 8-байтовый плейсхолдер по смещению `instrGlobalOffset`.
                    ASMInstructions.PatchMovwMovt(binary, instrGlobalOffset, targetAddr);
                }
            }

            return binary;
        }
    }
}
