using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

        public override void GenerateForEachStatement(MethodCompilationContext context, ForEachStatementSyntax node, Action generateBody, Action<string, string> registerLoopContext, Action popLoopContext)
        {
            string startLabel = context.NextLabel("FOREACH_START");
            string endLabel = context.NextLabel("FOREACH_END");
            string incLabel = context.NextLabel("FOREACH_INC");

            registerLoopContext(endLabel, incLabel);

            var collectionType = context.SemanticModel.GetTypeInfo(node.Expression).Type;
            bool isArray = collectionType?.TypeKind == TypeKind.Array;

            int indexReg = 0;
            int lengthReg = 0;
            int collectionReg = 0;
            int enumReg = 0;
            int itemReg = context.NextFreeRegister++;
            context.RegisterMap[node.Identifier.Text] = itemReg;

            if (isArray)
            {
                collectionReg = context.NextFreeRegister++;
                ASMInstructions.EmitExpression(node.Expression, collectionReg, context);

                indexReg = context.NextFreeRegister++;
                ASMInstructions.EmitMovImmediate(indexReg, 0, context);

                lengthReg = context.NextFreeRegister++;

                int fourReg = context.NextFreeRegister++;
                ASMInstructions.EmitMovImmediate(fourReg, 4, context);

                int lenAddrReg = context.NextFreeRegister++;
                ASMInstructions.EmitArithmeticOp(SyntaxKind.SubtractExpression, lenAddrReg, collectionReg, fourReg, context);
                context.Emit($"@ Read array length");
                ASMInstructions.EmitMemoryAccess(true, lengthReg, lenAddrReg, 0, context);
                context.NextFreeRegister -= 2;

                context.MarkLabel(startLabel);

                ASMInstructions.EmitCompare(indexReg, lengthReg, context);
                ASMInstructions.EmitBranch(endLabel, "GE", context);

                int offsetReg = context.NextFreeRegister++;
                ASMInstructions.EmitMovRegister(offsetReg, indexReg, context);
                int elemSizeReg = context.NextFreeRegister++;
                ASMInstructions.EmitMovImmediate(elemSizeReg, 4, context);
                ASMInstructions.EmitArithmeticOp(SyntaxKind.MultiplyExpression, offsetReg, offsetReg, elemSizeReg, context);

                int targetAddrReg = context.NextFreeRegister++;
                ASMInstructions.EmitMovRegister(targetAddrReg, collectionReg, context);
                ASMInstructions.EmitArithmeticOp(SyntaxKind.AddExpression, targetAddrReg, targetAddrReg, offsetReg, context);

                ASMInstructions.EmitMemoryAccess(true, itemReg, targetAddrReg, 0, context);
                context.NextFreeRegister -= 3;
            }
            else
            {
                var foreachInfo = context.SemanticModel.GetForEachStatementInfo(node);
                if (foreachInfo.GetEnumeratorMethod == null || foreachInfo.MoveNextMethod == null || foreachInfo.CurrentProperty == null)
                    throw new Exception("Не удалось разрешить GetEnumerator, MoveNext или Current для foreach");

                ASMInstructions.EmitExpression(node.Expression, 0, context);

                string getEnumTarget = foreachInfo.GetEnumeratorMethod.ToDisplayString();
                ASMInstructions.EmitCall(getEnumTarget, context, foreachInfo.GetEnumeratorMethod.IsStatic);

                enumReg = context.NextFreeRegister++;
                ASMInstructions.EmitMovRegister(enumReg, 0, context);

                context.MarkLabel(startLabel);

                ASMInstructions.EmitMovRegister(0, enumReg, context);
                string moveNextTarget = foreachInfo.MoveNextMethod.ToDisplayString();
                ASMInstructions.EmitCall(moveNextTarget, context, foreachInfo.MoveNextMethod.IsStatic);

                ASMInstructions.EmitCompareImmediate(0, 0, context);
                ASMInstructions.EmitBranch(endLabel, "EQ", context);

                ASMInstructions.EmitMovRegister(0, enumReg, context);
                string currentTarget = foreachInfo.CurrentProperty.GetMethod.ToDisplayString();
                ASMInstructions.EmitCall(currentTarget, context, foreachInfo.CurrentProperty.IsStatic);

                ASMInstructions.EmitMovRegister(itemReg, 0, context);
            }

            generateBody();

            context.MarkLabel(incLabel);

            if (isArray)
            {
                ASMInstructions.EmitOpWithImmediate(SyntaxKind.AddExpression, indexReg, indexReg, 1, context);
            }

            ASMInstructions.EmitJump(startLabel, context);

            context.MarkLabel(endLabel);

            popLoopContext();

            if (isArray)
            {
                context.NextFreeRegister -= 3; // lengthReg, indexReg, collectionReg
            }
            else
            {
                context.NextFreeRegister -= 1; // enumReg
            }
            context.NextFreeRegister--; // itemReg
        }

        public override void GenerateBreakStatement(MethodCompilationContext context, string breakLabel)
        {
            ASMInstructions.EmitJump(breakLabel, context);
        }

        public override void GenerateContinueStatement(MethodCompilationContext context, string continueLabel)
        {
            ASMInstructions.EmitJump(continueLabel, context);
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

        public override void EmitVariableAllocation(MethodCompilationContext context, VariableAllocationContext allocContext)
        {
            if (allocContext.IsStack)
            {
                context.Emit($"@ Allocation: {allocContext.VarName} -> Stack[{allocContext.StackOffset}]");
                if (allocContext.HasInitializer)
                {
                    ASMInstructions.EmitMemoryAccess(false, allocContext.InitValueReg, 13, allocContext.StackOffset, context);
                }
            }
            else
            {
                context.Emit($"@ Allocation: {allocContext.VarName} -> r{allocContext.RegisterIndex}");
            }
        }

        public override void EmitStoreToArrayElement(MethodCompilationContext context, int elementSize, int valueReg, int destAddrReg)
        {
            if (elementSize == 1)
            {
                context.Emit($"STRB r{valueReg}, [r{destAddrReg}, #0]");
                context.Write16((ushort)(0x7000 | ((destAddrReg & 0x7) << 3) | (valueReg & 0x7)));
            }
            else if (elementSize == 2)
            {
                context.Emit($"STRH r{valueReg}, [r{destAddrReg}, #0]");
                context.Write16((ushort)(0x8000 | ((destAddrReg & 0x7) << 3) | (valueReg & 0x7)));
            }
            else
            {
                context.Emit($"STR r{valueReg}, [r{destAddrReg}, #0]");
                context.Write16((ushort)(0x6000 | ((destAddrReg & 0x7) << 3) | (valueReg & 0x7)));
            }
        }

        public override void EmitLoadFromArrayElement(MethodCompilationContext context, int elementSize, int resultReg, int sourceAddrReg)
        {
            if (elementSize == 1)
            {
                context.Emit($"LDRB r{resultReg}, [r{sourceAddrReg}, #0]");
                context.Write16((ushort)(0x7800 | ((sourceAddrReg & 0x7) << 3) | (resultReg & 0x7)));
            }
            else if (elementSize == 2)
            {
                context.Emit($"LDRH r{resultReg}, [r{sourceAddrReg}, #0]");
                context.Write16((ushort)(0x8800 | ((sourceAddrReg & 0x7) << 3) | (resultReg & 0x7)));
            }
            else
            {
                context.Emit($"LDR r{resultReg}, [r{sourceAddrReg}, #0]");
                context.Write16((ushort)(0x6800 | ((sourceAddrReg & 0x7) << 3) | (resultReg & 0x7)));
            }
        }

        public override void GeneratePrefixUnaryExpression(MethodCompilationContext context, PrefixUnaryExpressionSyntax node)
        {
            ASMInstructions.EmitExpression(node, 0, context);
        }

        public override void GeneratePostfixUnaryExpression(MethodCompilationContext context, PostfixUnaryExpressionSyntax node)
        {
            ASMInstructions.EmitExpression(node, 0, context);
        }

        public override void GenerateLiteralExpression(MethodCompilationContext context, LiteralExpressionSyntax node)
        {
            ASMInstructions.EmitExpression(node, 0, context);
        }

        public override void GenerateIdentifierName(MethodCompilationContext context, IdentifierNameSyntax node)
        {
            ASMInstructions.EmitExpression(node, 0, context);
        }

        public override void GenerateInvocationExpression(MethodCompilationContext context, InvocationExpressionSyntax node)
        {
            ASMInstructions.EmitExpression(node, 0, context);
        }
    }
}