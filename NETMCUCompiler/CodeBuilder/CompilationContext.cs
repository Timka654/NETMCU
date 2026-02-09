using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using System.Xml.Linq;

namespace NETMCUCompiler.CodeBuilder
{
    public class CompilationContext : BaseCompilationContext
    {
        public required SemanticModel SemanticModel { get; set; }

        public TypeDeclarationSyntax[] ExceptTypes { get; set; } = [];

        public MethodDeclarationSyntax[] ExceptMethods { get; set; } = [];

        public required ClassDeclarationSyntax? ProgramClass { get; set; }

        public required MethodDeclarationSyntax? MainMethod { get; set; }

        //public required LinkerContext[] LinkerContexts { get; set; }

        public required BuildingContext BuildingContext { get; init; }

        public string BinaryPath { get; set; }

        public override CompilationContextTypeEnum ContextType => CompilationContextTypeEnum.Global;

        //public void RegisterType(string name, TypeDeclarationSyntax node)
        //{
        //    if (LinkerContexts == null) return;

        //    var meta = new TypeMetadata { Name = name, IsClass = true };
        //    int currentOffset = 0;

        //    foreach (var member in node.Members.OfType<FieldDeclarationSyntax>())
        //    {
        //        foreach (var variable in member.Declaration.Variables)
        //        {
        //            meta.FieldOffsets[variable.Identifier.Text] = currentOffset;
        //            currentOffset += 4; // Пока считаем всё по 4 байта (int, uint, ptr)
        //        }
        //    }
        //    meta.TotalSize = currentOffset;

        //    foreach (var c in LinkerContexts)
        //    {
        //        if (c.OutputTypes.ContainsKey(meta.Name)) throw new Exception($"Дублирование имени {meta.Name}");
        //        c.OutputTypes[meta.Name] = meta;
        //    }
        //}

        private Dictionary<string, object> ConstantMap { get; } = new();

        public KeyValuePair<string, object>[] GetConstantMap()
            => ConstantMap.ToArray();

        public void FillConstants(KeyValuePair<string, object>[] another)
        {
            foreach (var item in another)
            {
                RegisterConstant(item.Key, item.Value);
            }
        }

        public void RegisterConstant(string name, object value)
        {
            ConstantMap[name] = value;
            return;
        }

        public bool TryGetConstant(string name, out object value)
        {
            if (ConstantMap.TryGetValue(name, out value)) return true;

            return false;
        }

        public bool TryGetConstant(ExpressionSyntax syntax, out object value)
        {
            var t = SemanticModel.GetSymbolInfo(syntax);

            if (TryGetConstant(t.Symbol.ToDisplayString(), out value)) return true;

            return false;
        }



        public ConcurrentDictionary<string, List<RelocationRecord>> NativeRelocations { get; } = new();
        public ConcurrentDictionary<string, List<RelocationRecord>> ReferenceRelocations { get; } = new();

        public void AddRelocation(MethodCompilationContext context, string name, bool isStatic, bool isNative, int offset)
        {
            var i = new RelocationRecord(context, offset, isStatic);

            if (isNative)
                NativeRelocations.GetOrAdd(name, _ => new List<RelocationRecord>())
                    .Add(i);
            else
                ReferenceRelocations.GetOrAdd(name, _ => new List<RelocationRecord>())
                    .Add(i);
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
