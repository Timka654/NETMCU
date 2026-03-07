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

        public override void GenerateTryStatement(MethodCompilationContext context, Action generateTryBlock, Action<CatchClauseSyntax> generateCatchBlock, Action generateFinallyBlock, SyntaxList<CatchClauseSyntax> catches, FinallyClauseSyntax finallyClause)
        {
            context.Emit("@ TRY BLOCK START");

            string catchLabel = context.NextLabel("CATCH_HANDLER");
            string endLabel = context.NextLabel("TRY_END");

            // Помещаем адрес обработчика в r0 и вызываем NETMCU_TryPush
            context.Emit($"ADR.W r0, {catchLabel}");
            context.AddLoadAddress(catchLabel);
            context.Write32(0xF2AF0000); // Placeholder for ADR.W r0, label

            ASMInstructions.EmitCall("NETMCU_TryPush", context, isStatic: true, isNative: true);

            generateTryBlock();

            // Если блок выполнился успешно без throw, снимаем обработчик
            ASMInstructions.EmitCall("NETMCU_TryPop", context, isStatic: true, isNative: true);
            context.Emit($"B {endLabel}");
            context.AddJump(endLabel, false);
            context.Write16(0xE000); // branch empty

            // Сам функция-обработчик catch (вызывается из throw)
            context.MarkLabel(catchLabel);

            // Снимаем себя из стека обработчиков, чтобы следующий throw полетел выше
            ASMInstructions.EmitCall("NETMCU_TryPop", context, isStatic: true, isNative: true); 

            // Обрабатываем блоки catch
            foreach (var catchClause in catches)
            {
                context.Emit($"@ CATCH BLOCK: {catchClause.Declaration?.Type?.ToString()}");
                generateCatchBlock(catchClause);
            }

            // Возврат в throw! (как и просил пользователь, без сложной раскрутки)
            context.Emit($"B {endLabel}");
            context.AddJump(endLabel, false);
            context.Write16(0xE000); // branch empty

            context.MarkLabel(endLabel);

            if (finallyClause != null)
            {
                context.Emit("@ FINALLY BLOCK");
                generateFinallyBlock();
            }

            context.Emit("@ TRY BLOCK END");
        }

        public override void GenerateThrowStatement(MethodCompilationContext context, ExpressionSyntax expression)
        {
            context.Emit("@ THROW EXECUTION");
            if (expression != null)
            {
                // Помещаем результат выражения сразу в r0 (первый аргумент для вызова)
                ASMInstructions.EmitExpression(expression, 0, context);
            }
            else
            {
                context.Emit("mov r0, #0 @ Rethrow or null exception");
                ASMInstructions.EmitMovImmediate(0, 0, context);
            }

            ASMInstructions.EmitCall("NETMCU_Throw", context, isStatic: true, isNative: true);
        }

        public override void GenerateReturnStatement(MethodCompilationContext context, ExpressionSyntax expression)
        {
            if (expression != null)
            {
                // Результат возврата должен лечь в R0
                ASMInstructions.EmitExpression(expression, 0, context);
            }

            // Прыгаем в эпилог текущего метода (метода, который будет генерировать POP {pc})
            string methodName = context.Name;
            ASMInstructions.EmitJump($"{methodName}_exit", context);
        }

        public override void GenerateSwitchStatement(MethodCompilationContext context, ExpressionSyntax expression, SyntaxList<SwitchSectionSyntax> sections, Action<SwitchSectionSyntax> generateSectionBody, Action<string, string> registerLoopContext, Action popLoopContext)
        {
            int switchReg = context.NextFreeRegister++;
            ASMInstructions.EmitExpression(expression, switchReg, context);

            string endSwitchLabel = context.NextLabel("SWITCH_END");

            // В C# break внутри switch должен выйти из switch.
            registerLoopContext(endSwitchLabel, "ERROR_CONTINUE_IN_SWITCH");

            int tmpReg = context.NextFreeRegister++;
            string defaultLabel = null;

            var sectionLabels = new Dictionary<SwitchSectionSyntax, string>();

            // 1. Создаем метки для блоков и генерируем проверки кейсов
            foreach (var section in sections)
            {
                string sectionLabel = context.NextLabel("SWITCH_SECTION");
                sectionLabels[section] = sectionLabel;

                foreach (var label in section.Labels)
                {
                    if (label is DefaultSwitchLabelSyntax)
                    {
                        defaultLabel = sectionLabel;
                    }
                    else if (label is CaseSwitchLabelSyntax caseLabel)
                    {
                        // Сравниваем
                        ASMInstructions.EmitExpression(caseLabel.Value, tmpReg, context);
                        ASMInstructions.EmitCompare(switchReg, tmpReg, context);
                        ASMInstructions.EmitBranch(sectionLabel, "EQ", context);
                    }
                }
            }

            // 2. Если ни один не подошел, и есть default:
            if (defaultLabel != null)
            {
                ASMInstructions.EmitJump(defaultLabel, context);
            }
            else
            {
                // Если нет default, прыгаем в конец
                ASMInstructions.EmitJump(endSwitchLabel, context);
            }

            // 3. Вывод тел кейсов
            foreach (var section in sections)
            {
                context.MarkLabel(sectionLabels[section]);
                generateSectionBody(section);
            }

            // Конец
            context.MarkLabel(endSwitchLabel);
            popLoopContext();

            // Освобождаем регистры
            context.NextFreeRegister -= 2; 
        }
    }
}