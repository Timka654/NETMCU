using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;

namespace NETMCUCompiler.CodeBuilder.Backends
{
    public abstract class MCUBackend
    {
        public abstract TypeCompilationContext CreateTypeContext(TypeDeclarationSyntax type, SemanticModel semanticModel, CompilationContext global, string name);
        public abstract MethodCompilationContext CreateMethodContext(SyntaxNode methodSyntax, IMethodSymbol symbol, BaseCompilationContext parentContext, string name);

        public abstract void GenerateMethodPrologue(MethodCompilationContext context, bool isInstance, ImmutableArray<IParameterSymbol> parameters);
        public abstract void GenerateMethodEpilogue(MethodCompilationContext context);

        public abstract void GenerateIfStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateTrueBlock, Action generateFalseBlock);

        public abstract void GenerateWhileStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateBody, Action<string, string> registerLoopContext, Action popLoopContext);

        public abstract void GenerateDoStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateBody, Action<string, string> registerLoopContext, Action popLoopContext);

        public abstract void GenerateForStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateInit, Action generateBody, Action generateIncrementor, Action<string, string> registerLoopContext, Action popLoopContext);
    }
}