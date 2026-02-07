using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace NETMCUCompiler.CodeBuilder
{
    // В CompilationContext.cs добавь:
    public class StackVariable
    {
        public string Name { get; set; }
        public TypeMetadata Metadata { get; set; }
        public int StackOffset { get; set; } // Смещение от SP (указателя стека)
    }

    public class CompilationContext
    {
        public StringBuilder Asm { get; } = new();
        public MemoryStream Bin { get; } = new();
        public Dictionary<string, int> RegisterMap { get; } = new();
        public Dictionary<string, int> ConstantMap { get; } = new();

        public TypeDeclarationSyntax[] ExceptClasses { get; set; } = [];

        public MethodDeclarationSyntax[] ExceptMethods { get; set; } = [];

        public required ClassDeclarationSyntax? ProgramClass { get; set; }
        public required MethodDeclarationSyntax? MainMethod { get; set; }

        public required LinkerContext[] LinkerContexts { get; set; }

        public required BuildingContext BuildingContext { get; init; }

        public string BinaryPath { get; set; }

        public int LabelCount = 0;
        public int LastUsedRegister { get; set; } = 4; // Текущий "рабочий" регистр
        // Вспомогательные методы, чтобы не писать каждый раз Asm.AppendLine
        public void Emit(string line) => Asm.AppendLine($"    {line}");

        public void Bytecode(byte code) => Bin.WriteByte(code);

        // Вспомогательные методы, чтобы не путаться в байтах
        public void Write16(ushort val)
        {
            Bytecode((byte)(val & 0xFF));
            Bytecode((byte)(val >> 8));
        }

        // Исправленный Write32 для Thumb-2 Wide Instructions
        public void Write32(uint val)
        {
            // Сначала пишем High Halfword, потом Low Halfword
            ushort high = (ushort)(val >> 16);
            ushort low = (ushort)(val & 0xFFFF);
            Write16(high);
            Write16(low);
        }

        public string NextLabel(string prefix) => $"L_{prefix}_{LabelCount++}";
        public int NextFreeRegister = 4; // Пул R4-R11


        // Вспомогательный метод для получения регистра переменной
        public int GetVarRegister(string name)
        {
            if (RegisterMap.TryGetValue(name, out int reg)) return reg;
            throw new Exception($"Переменная {name} не объявлена");
        }

        public void AddRelocation(string name, bool isStatic)
        {
            var offset = (int)Bin.Position;

            foreach (var c in LinkerContexts)
            {
                if (!c.InputMethods.ContainsKey(name))
                    c.InputMethods[name] = new ();

                // Запоминаем текущую позицию в бинарном потоке
                c.InputMethods[name].Add(new RelocationRecord(this, offset, isStatic));
            }
        }

        public void RegisterType(TypeDeclarationSyntax node, SemanticModel semanticModel)
        {
            if (LinkerContexts == null) return;

            var classSymbol = semanticModel.GetDeclaredSymbol(node) as INamedTypeSymbol;

            var className = classSymbol.ToDisplayString();

            var meta = new TypeMetadata { Name = className, IsClass = true };
            int currentOffset = 0;

            foreach (var member in node.Members.OfType<FieldDeclarationSyntax>())
            {
                foreach (var variable in member.Declaration.Variables)
                {
                    meta.FieldOffsets[variable.Identifier.Text] = currentOffset;
                    currentOffset += 4; // Пока считаем всё по 4 байта (int, uint, ptr)
                }
            }
            meta.TotalSize = currentOffset;

            foreach (var c in LinkerContexts)
            {
                if(c.OutputTypes.ContainsKey(meta.Name)) throw new Exception($"Дублирование имени {meta.Name}");
                c.OutputTypes[meta.Name] = meta;
            }
        }

        public void RegisterMethod(SemanticModel model, TypeDeclarationSyntax cls, MethodDeclarationSyntax method, bool isStatic)
        {
            var classSymbol = model.GetDeclaredSymbol(cls) as INamedTypeSymbol;
            var methodSymbol = model.GetDeclaredSymbol(method) as IMethodSymbol;

            // Полное имя: Namespace.ClassName.MethodName
            string fullName = methodSymbol.ToDisplayString();

            var position = (int)Bin.Position;
            // Регистрируем точку входа в ExportMap
            foreach (var c in LinkerContexts)
            {
                c.OutputMethods[fullName] = new LinkerRecord(this, position, isStatic) ;
            }
        }

        // Храним переменные, которые живут на стеке
        public Dictionary<string, StackVariable> StackMap { get; } = new();

        private int _currentStackPointer = 0;

        public void AllocateOnStack(string name, string typeName)
        {
            foreach (var c in LinkerContexts)
            {
                if (c.OutputTypes.TryGetValue(typeName, out var meta))
                {
                    StackMap[name] = new StackVariable
                    {
                        Name = name,
                        Metadata = meta,
                        StackOffset = _currentStackPointer
                    };
                    _currentStackPointer += meta.TotalSize;

                    // Важно: в будущем нам нужно будет вычесть _currentStackPointer из SP 
                    // в начале метода (Prologue), чтобы зарезервировать место.
                }
            }
        }

        public override string ToString()
        {
            return BuildingContext.Path;
        }
    }



    public class TypeMetadata
    {
        public string Name { get; set; }
        public int TotalSize { get; set; }
        public Dictionary<string, int> FieldOffsets { get; } = new();
        public bool IsClass { get; set; } // true - куча, false - стек (struct)
    }
}
