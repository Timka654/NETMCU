using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Xml.Linq;

namespace NETMCUCompiler.CodeBuilder
{
    public class TypeCompilationContext : BaseCompilationContext
    {
        public TypeDeclarationSyntax TypeSyntax { get; }

        public required string Name { get; set; }

        public bool IsPublic { get; }

        public bool IsClass { get; }

        public int Size { get; }

        public IReadOnlyDictionary<string, int> FieldOffsets { get; }

        public TypeCompilationContext(TypeDeclarationSyntax type)
        {
            TypeSyntax = type;

            int currentOffset = 0;

            Dictionary<string, int> fieldOffsets = new();

            foreach (var member in TypeSyntax.Members.OfType<FieldDeclarationSyntax>())
            {
                foreach (var variable in member.Declaration.Variables)
                {
                    fieldOffsets[variable.Identifier.Text] = currentOffset;
                    currentOffset += 4; // Пока считаем всё по 4 байта (int, uint, ptr)
                }
            }

            FieldOffsets = fieldOffsets;
            Size = currentOffset;

            IsClass = TypeSyntax is ClassDeclarationSyntax;
            IsPublic = TypeSyntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
        }

        public override CompilationContextTypeEnum ContextType => CompilationContextTypeEnum.Type;

        public required SemanticModel SemanticModel { get; init; }

        public CompilationContext Global => ParentContext as CompilationContext;

        private Dictionary<string, object> ConstantMap { get; } = new();

        public void RegisterConstant(string name, object value)
        {
            ConstantMap[name] = value;
        }

        public bool TryGetConstant(string name, out object value)
        {
            if (ConstantMap.TryGetValue(name, out value)) return true;
            return Global.TryGetConstant(name, out value);
        }

        public bool TryGetConstant(ExpressionSyntax syntax, out object value)
        {
            var t = SemanticModel.GetSymbolInfo(syntax);

            if (TryGetConstant(t.Symbol.ToDisplayString(), out value)) return true;

            return false;
        }
    }
}
