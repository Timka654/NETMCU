using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace NETMCUCompiler.CodeBuilder
{
    public class CompilationContext
    {
        public StringBuilder Asm { get; } = new();
        public MemoryStream Bin { get; } = new();
        public Dictionary<string, int> RegisterMap { get; } = new();
        public Dictionary<string, int> ConstantMap { get; } = new();

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

        public Dictionary<string, int> ExportMap { get; } = new();

        public Dictionary<string, List<int>> NativeRelocations { get; } = new();

        public void AddRelocation(string name)
        {
            if (!NativeRelocations.ContainsKey(name))
                NativeRelocations[name] = new List<int>();

            // Запоминаем текущую позицию в бинарном потоке
            NativeRelocations[name].Add((int)Bin.Position);
        }

        public TypeManager TypeManager { get; } = new();
    }



    public class TypeMetadata
    {
        public string Name { get; set; }
        public int TotalSize { get; set; }
        public Dictionary<string, int> FieldOffsets { get; } = new();
        public bool IsClass { get; set; } // true - куча, false - стек (struct)
    }

    public class TypeManager
    {
        private readonly Dictionary<string, TypeMetadata> _types = new();

        public void RegisterType(ClassDeclarationSyntax node)
        {
            var meta = new TypeMetadata { Name = node.Identifier.Text, IsClass = true };
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
            _types[meta.Name] = meta;
        }

        public TypeMetadata GetType(string name) => _types.TryGetValue(name, out var m) ? m : null;
    }
}
