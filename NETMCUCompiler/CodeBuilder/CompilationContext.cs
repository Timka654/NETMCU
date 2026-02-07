using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace NETMCUCompiler.CodeBuilder
{
    public class CompilationContext
    {
        public StringBuilder Asm { get; } = new();
        public MemoryStream Bin { get; } = new();
        public Dictionary<string, int> RegisterMap { get; } = new();
        public Dictionary<string, int> ConstantMap { get; } = new();

        public ClassDeclarationSyntax[] ExceptClasses { get; set; } = [];
        public MethodDeclarationSyntax[] ExceptMethods { get; set; } = [];

        public required LinkerContext[] LinkerContexts { get; set; }

        public required BuildingContext BuildingContext { get; init; }

        public string BinaryPath { get; set; }

        public int LabelCount = 0;

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

        public void RegisterType(ClassDeclarationSyntax node, SemanticModel semanticModel)
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

        public void RegisterMethod(SemanticModel model, ClassDeclarationSyntax cls, MethodDeclarationSyntax method, bool isStatic)
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
    }



    public class TypeMetadata
    {
        public string Name { get; set; }
        public int TotalSize { get; set; }
        public Dictionary<string, int> FieldOffsets { get; } = new();
        public bool IsClass { get; set; } // true - куча, false - стек (struct)
    }
}
