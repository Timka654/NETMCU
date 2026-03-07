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

        public override void GenerateIfStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateTrueBlock, Action generateFalseBlock)
        {
            string trueLabel = $"L_TRUE_{context.LabelCount++}";
            string falseLabel = $"L_FALSE_{context.LabelCount++}";
            string endLabel = $"L_END_{context.LabelCount++}";

            // Разбираем цепочку условий
            ASMInstructions.EmitLogicalCondition(condition, trueLabel, falseLabel, context);

            // Тело TRUE
            context.MarkLabel(trueLabel);
            generateTrueBlock();
            ASMInstructions.EmitJump(endLabel, context);

            // Тело FALSE (или следующий Else If)
            context.MarkLabel(falseLabel);
            if (generateFalseBlock != null)
            {
                generateFalseBlock();
            }

            context.MarkLabel(endLabel);
        }

        public override void GenerateWhileStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateBody, Action<string, string> registerLoopContext, Action popLoopContext)
        {
            string startLabel = context.NextLabel("WHILE_START");
            string endLabel = context.NextLabel("WHILE_END");

            registerLoopContext(endLabel, startLabel);

            context.MarkLabel(startLabel);

            ASMInstructions.EmitLogicalCondition(condition, "", endLabel, context);

            generateBody();

            ASMInstructions.EmitJump(startLabel, context);

            context.MarkLabel(endLabel);

            popLoopContext();
        }
    }
}