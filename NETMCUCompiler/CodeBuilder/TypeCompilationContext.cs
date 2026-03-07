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

        public bool CompilerType { get; }

        public bool IsClass { get; }

        public int Size { get; }

        public IReadOnlyDictionary<string, int> FieldOffsets { get; }

        public TypeCompilationContext(TypeDeclarationSyntax type, SemanticModel semanticModel, CompilationContext global) : base(global)
        {
            ParentContext = global;
            TypeSyntax = type;
            SemanticModel = semanticModel;

            int currentOffset = global.BuildingContext.Options?.TypeHeader == true && type is ClassDeclarationSyntax ? 4 : 0;

            Dictionary<string, int> fieldOffsets = new();

            foreach (var member in TypeSyntax.Members.OfType<FieldDeclarationSyntax>())
            {
                var typeInfo = semanticModel.GetTypeInfo(member.Declaration.Type).Type;
                int fieldSize = 4;
                int align = 4;
                if (typeInfo != null)
                {
                    if (typeInfo.SpecialType == SpecialType.System_Boolean || typeInfo.SpecialType == SpecialType.System_Byte || typeInfo.SpecialType == SpecialType.System_SByte)
                    {
                        fieldSize = 1; align = 1;
                    }
                    else if (typeInfo.SpecialType == SpecialType.System_Int16 || typeInfo.SpecialType == SpecialType.System_UInt16 || typeInfo.SpecialType == SpecialType.System_Char)
                    {
                        fieldSize = 2; align = 2;
                    }
                    else if (typeInfo.SpecialType == SpecialType.System_Int64 || typeInfo.SpecialType == SpecialType.System_UInt64 || typeInfo.SpecialType == SpecialType.System_Double)
                    {
                        fieldSize = 8; align = 8;
                    }
                    else if (typeInfo.TypeKind == TypeKind.Struct)
                    {
                        // Struct sizes should ideally be precomputed or recursive, for now use a naive sum if needed or just 4 as default
                        // In real compiler we need to resolve it by getting the matching TypeCompilationContext Size
                        // For safe fallback we keep 4, it should be updated but for simple types above this works
                    }
                }

                foreach (var variable in member.Declaration.Variables)
                {
                    // Align offset
                    currentOffset = (currentOffset + align - 1) & ~(align - 1);
                    fieldOffsets[variable.Identifier.Text] = currentOffset;
                    currentOffset += fieldSize;
                }
            }

            // Align final size to 4 bytes
            currentOffset = (currentOffset + 3) & ~3;

            FieldOffsets = fieldOffsets;
            Size = currentOffset;

            IsClass = TypeSyntax is ClassDeclarationSyntax;
            IsPublic = TypeSyntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
            CompilerType = TypeSyntax.AttributeLists.SelectMany(a => a.Attributes)
                .Any(a => a.Name.ToString() == "CompilerType" || a.Name.ToString() == "CompilerTypeAttribute");
        }

        public override CompilationContextTypeEnum ContextType => CompilationContextTypeEnum.Type;

        public SemanticModel SemanticModel { get; }

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
            var constOpt = SemanticModel.GetConstantValue(syntax);
            if (constOpt.HasValue) 
            {
                value = constOpt.Value;
                return true;
            }

            var t = SemanticModel.GetSymbolInfo(syntax);

            if (t.Symbol != null && TryGetConstant(t.Symbol.ToDisplayString(), out value)) return true;

            value = null;
            return false;
        }
    }
}
