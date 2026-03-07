using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;

namespace NETMCUCompiler.CodeBuilder.Backends
{
    public class CortexM4Backend : MCUBackend
    {
        public override TypeCompilationContext CreateTypeContext(TypeDeclarationSyntax type, SemanticModel semanticModel, CompilationContext global, string name)
        {
            return new TypeCompilationContext(type, semanticModel, global) { Name = name };
        }

        public override MethodCompilationContext CreateMethodContext(SyntaxNode methodSyntax, IMethodSymbol symbol, BaseCompilationContext parentContext, string name)
        {
            return new MethodCompilationContext(methodSyntax, symbol, parentContext) { Name = name };
        }

        public override void GenerateMethodPrologue(MethodCompilationContext context, bool isInstance, ImmutableArray<IParameterSymbol> parameters)
        {
            ASMInstructions.EmitMethodPrologue(isInstance, parameters, context);
        }

        public override void GenerateMethodEpilogue(MethodCompilationContext context)
        {
            ASMInstructions.EmitMethodEpilogue(context);
        }
    }
}