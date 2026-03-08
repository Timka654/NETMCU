using Microsoft.CodeAnalysis;
using NETMCUCompiler.CodeBuilder;
using NETMCUCompiler.Shared.Compilation;
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
                OutputTypes = compilationContext.Childs.Values
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
                .Concat(Methods)
                .ToDictionary(x => x.Name, x => x);

            var relocs = compilationContext.ReferenceRelocations;

            foreach (var item in relocs.ToArray())
            {
                if (!refMethods.TryGetValue(item.Key, out var method))
                    throw new Exception($"Ref methods does not contains {item.Key}");

                foreach (var x in item.Value)
                {
                    compilationContext.Backend.PatchCall(builtImage, (int)(x.Context.BinaryOffset! + x.Offset), method.BinaryOffset!.Value);

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

            compilationContext = new CompilationContext(null)
            {
                Backend = backend,
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

            Compilation = CodeBuilder.RecordExpander.Expand(Compilation);
            Compilation = CodeBuilder.AOTGenericExpander.Expand(Compilation);

            LibraryCompiler.CompileProject(Compilation, compilationContext);

            DumpMeta();

            if (type == BuildingOutputTypeEnum.Executable)
                DumpApplication();
            else
                DumpLibrary();

            DumpLinker();

            return true;
        }


        private byte[] BuildRoDataSection(out Dictionary<string, uint> offsets, out List<(int OffsetInSection, string TargetSymbol)> rodataPatches)
        {
            offsets = new Dictionary<string, uint>();
            rodataPatches = new List<(int OffsetInSection, string TargetSymbol)>();

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

                if (Options?.TypeHeader == true)
                {
                    // 1. Type Metadata Pointer
                    rodataPatches.Add(((int)ms.Position, "__type_literal_string"));
                    writer.Write((uint)0); // Placeholder

                    // 2. Length (in chars)
                    writer.Write((uint)literal.Value.Length);

                    // 3. UTF-16 Chars
                    var bytes = Encoding.Unicode.GetBytes(literal.Value); // UTF-16 LE
                    writer.Write(bytes);
                    writer.Write((short)0); // 16-bit null terminator for safety
                }
                else
                {
                    writer.Write((uint)literal.Value.Length); // Add a 4-byte length even without header for basic operations
                    var bytes = Encoding.Unicode.GetBytes(literal.Value);
                    writer.Write(bytes);
                    writer.Write((short)0); // Null terminator
                }
            }

            return ms.ToArray();
        }

        private byte[] BuildTypeDataSection(out Dictionary<string, uint> offsets, Dictionary<string, uint> globalSymbolTable, List<(int OffsetInSection, string TargetMethod)> vtablePatches)
        {
            offsets = new Dictionary<string, uint>();
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            foreach (var t in compilationContext.TypeLiterals.OrderBy(x => x.Key))
            {
                while (ms.Position % 4 != 0)
                {
                    writer.Write((byte)0);
                }

                offsets[t.Key] = (uint)ms.Position;

                bool keepMetadata = Options.TypeMetaDataLevel == "Full";
                if (!keepMetadata && Options.TypeMetaDataLevel == "Custom")
                {
                    keepMetadata = t.Value.GetAttributes().Any(a => 
                        a.AttributeClass?.Name == "KeepMetadataAttribute" || 
                        a.AttributeClass?.Name == "KeepMetadata");
                }

                int fieldsCount = t.Value.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Count();
                int size = fieldsCount * 4;
                if (Options.TypeHeader && t.Value.IsReferenceType) size += 4;
                if (size == 0) size = 4;

                writer.Write((uint)size);

                if (keepMetadata)
                {
                    string stringSymbol = compilationContext.GetStringLiteralSymbol(t.Value.ToDisplayString());
                    if (stringSymbol != null && globalSymbolTable.TryGetValue(stringSymbol, out uint nameAddr))
                    {
                        writer.Write((uint)nameAddr);
                    }
                    else
                    {
                        writer.Write((uint)0);
                    }
                }
                else
                {
                    writer.Write((uint)0);
                }

                // VTable Output for classes
                if (t.Value.IsReferenceType)
                {
                    var vtable = VTableBuilder.GetVTable(t.Value);
                    writer.Write((uint)vtable.Count); // size of vtable

                    foreach(var method in vtable)
                    {
                        string methodName = method.ToDisplayString(); // This matches the key registered in Global Symbol Table

                        // We record the current offset to patch the pointer later
                        vtablePatches.Add(((int)ms.Position, methodName));
                        writer.Write((uint)0); // Placeholder 4 bytes for the pointer
                    }
                }
                else
                {
                    writer.Write((uint)0); // VTable Count = 0
                }

                // Interface Table
                var allInterfaces = t.Value.AllInterfaces;
                writer.Write((uint)allInterfaces.Length); // Interface Count

                foreach (var iface in allInterfaces)
                {
                    // InterfaceTypePtr
                    var ifaceSymbolId = compilationContext.RegisterTypeLiteral(iface);
                    vtablePatches.Add(((int)ms.Position, ifaceSymbolId));
                    writer.Write((uint)0); // Placeholder 4 bytes for interface ptr

                    var ifaceMethods = iface.GetMembers().OfType<IMethodSymbol>().ToList();
                    writer.Write((uint)ifaceMethods.Count); // MethodCount

                    foreach (var imethod in ifaceMethods)
                    {
                        var implMethod = t.Value.FindImplementationForInterfaceMember(imethod) as IMethodSymbol;
                        if (implMethod != null && !implMethod.IsAbstract)
                        {
                            var implName = implMethod.ToDisplayString();
                            vtablePatches.Add(((int)ms.Position, implName));
                        }
                        writer.Write((uint)0); // Placeholder 4 bytes for method ptr
                    }
                }
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
                    // For Thumb mode (Cortex-M), method pointers must have the lowest bit set to 1.
                    // This ensures BLX <reg> successfully switches to Thumb state (which must always occur).
                    if (!globalSymbolTable.TryAdd(item.Name, (uint)(libraryBaseAddr + item.BinaryOffset + 1))) throw new Exception($"Duplicate symbol in global symbol table: {item.Name}");
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
            // --- СОЗДАНИЕ СЕКЦИИ .RODATA ---
            var roDataBytes = BuildRoDataSection(out var roDataOffsets, out var rodataPatches);

            // Выравниваем текущую позицию в прошивке
            while (finalImage.Position % 4 != 0) finalImage.WriteByte(0);

            // Запоминаем абсолютный адрес начала секции .rodata
            uint roDataSectionAddr = flashBaseAddress + (uint)finalImage.Position;

            // Добавляем символы строк в глобальную таблицу символов
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

            // --- СОЗДАНИЕ СЕКЦИИ .TYPEDATA ---
            List<(int OffsetInSection, string TargetMethod)> vtablePatches = new();
            var typeDataBytes = BuildTypeDataSection(out var typeDataOffsets, globalSymbolTable, vtablePatches);

            // Оцениваем адрес typeData после rodata (с правильным выравниванием)
            uint projectedTypeDataOff = (uint)finalImage.Position + (uint)roDataBytes.Length;
            uint typeDataSectionAddr = flashBaseAddress + projectedTypeDataOff + ((4 - projectedTypeDataOff % 4) % 4);

            // Add typeData symbols to global table
            foreach (var entry in typeDataOffsets)
            {
                var symbolName = entry.Key;
                var offsetInSection = entry.Value;
                var absoluteAddr = typeDataSectionAddr + offsetInSection;
                if (!globalSymbolTable.TryAdd(symbolName, absoluteAddr))
                {
                    throw new Exception($"Duplicate symbol in global symbol table: {symbolName}");
                }
            }

            // Patch RoData Pointers (like TypeHeader for Strings)
            foreach (var patch in rodataPatches)
            {
                if (globalSymbolTable.TryGetValue(patch.TargetSymbol, out uint symAddr))
                {
                    roDataBytes[patch.OffsetInSection]     = (byte)(symAddr & 0xFF);
                    roDataBytes[patch.OffsetInSection + 1] = (byte)((symAddr >> 8) & 0xFF);
                    roDataBytes[patch.OffsetInSection + 2] = (byte)((symAddr >> 16) & 0xFF);
                    roDataBytes[patch.OffsetInSection + 3] = (byte)((symAddr >> 24) & 0xFF);
                }
            }

            // Записываем пропатченную секцию rodata
            finalImage.Write(roDataBytes, 0, roDataBytes.Length);

            // Выравниваем перед TypeData
            while (finalImage.Position % 4 != 0) finalImage.WriteByte(0);

            // Patch vtable pointers within typeDataBytes
            foreach(var patch in vtablePatches)
            {
                if (globalSymbolTable.TryGetValue(patch.TargetMethod, out uint methAddr))
                {
                    typeDataBytes[patch.OffsetInSection]     = (byte)(methAddr & 0xFF);
                    typeDataBytes[patch.OffsetInSection + 1] = (byte)((methAddr >> 8) & 0xFF);
                    typeDataBytes[patch.OffsetInSection + 2] = (byte)((methAddr >> 16) & 0xFF);
                    typeDataBytes[patch.OffsetInSection + 3] = (byte)((methAddr >> 24) & 0xFF);
                }
                else
                {
                    Console.WriteLine($"[WARNING] Reference {patch.TargetMethod} not found in global symbol table. Filling with 0.");
                }
            }

            // Записываем секцию TypeData
            finalImage.Write(typeDataBytes, 0, typeDataBytes.Length);

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
                    compilationContext.Backend.PatchCall(binary, instrGlobalOffset, jumpOffset);
                }
            }

            foreach (var inputGroup in dataRelocOffsets)
            {
                string symbolName = inputGroup.Key;

                if (!globalSymbolTable.TryGetValue(symbolName, out uint targetAddr))
                {
                    Console.WriteLine($"[DEBUG] Available global symbols related to type literals:");
                    foreach(var k in globalSymbolTable.Keys.Where(x => x.StartsWith("__type_literal_")))
                        Console.WriteLine($"  {k}");

                    throw new Exception($"Undefined data reference: {symbolName}");
                }

                foreach (var instrGlobalOffset in inputGroup.Value)
                {
                    // Здесь вам нужно будет вызвать новую функцию-патчер,
                    // которая запишет 32-битный адрес `targetAddr`
                    // в 8-байтовый плейсхолдер по смещению `instrGlobalOffset`.
                    compilationContext.Backend.PatchDataAddress(binary, instrGlobalOffset, targetAddr);
                }
            }

            return binary;
        }
    }
}
