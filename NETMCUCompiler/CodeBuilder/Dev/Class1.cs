using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Text;

namespace NETMCUCompiler.CodeBuilder.Dev
{
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
