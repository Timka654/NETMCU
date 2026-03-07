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

        public override void GenerateDoStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateBody, Action<string, string> registerLoopContext, Action popLoopContext)
        {
            string startLabel = context.NextLabel("DO_START");
            string endLabel = context.NextLabel("DO_END");
            string condLabel = context.NextLabel("DO_COND"); // Для continue

            registerLoopContext(endLabel, condLabel);

            context.MarkLabel(startLabel);

            generateBody();

            context.MarkLabel(condLabel);

            // Проверяем условие. Если true -> прыгаем в начало (startLabel).
            ASMInstructions.EmitLogicalCondition(condition, startLabel, endLabel, context);

            context.MarkLabel(endLabel);

            popLoopContext();
        }

        public override void GenerateForStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateInit, Action generateBody, Action generateIncrementor, Action<string, string> registerLoopContext, Action popLoopContext)
        {
            // 1. Инициализация (может быть объявление переменной или просто присваивание)
            generateInit?.Invoke();

            string startLabel = context.NextLabel("FOR_START");
            string endLabel = context.NextLabel("FOR_END");
            string incLabel = context.NextLabel("FOR_INC"); // Для continue

            registerLoopContext(endLabel, incLabel);

            // Метка начала
            context.MarkLabel(startLabel);

            // 2. Условие
            if (condition != null)
            {
                ASMInstructions.EmitLogicalCondition(condition, "", endLabel, context);
            }

            // 3. Тело
            generateBody?.Invoke();

            // Метка инкремента (сюда будет прыгать continue, если мы его реализуем)
            context.MarkLabel(incLabel);

            // 4. Инкремент
            generateIncrementor?.Invoke();

            // 5. Прыжок на начало
            ASMInstructions.EmitJump(startLabel, context);

            // Метка выхода
            context.MarkLabel(endLabel);

            popLoopContext();
        }
    }
}