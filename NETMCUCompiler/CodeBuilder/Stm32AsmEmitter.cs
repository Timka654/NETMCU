using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Security.Claims;

namespace NETMCUCompiler.CodeBuilder
{
    public class Stm32MethodBuilder(MethodCompilationContext context) : CSharpSyntaxWalker
    {
        private Stack<(string breakLabel, string continueLabel)> _loopContexts = new();

        public void VisitVariableDeclaration(VariableDeclarationSyntax node)
        {
            context.Class.Global.Backend.GenerateVariableDeclaration(context, node);
        }

        public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            VisitVariableDeclaration(node.Declaration);
        }
        public override void VisitForStatement(ForStatementSyntax node)
        {
            context.Class.Global.Backend.GenerateForStatement(context, node.Condition,
                generateInit: () => {
                    if (node.Declaration != null) VisitVariableDeclaration(node.Declaration);
                    foreach (var initializer in node.Initializers) Visit(initializer);
                },
                generateBody: () => Visit(node.Statement),
                generateIncrementor: () => {
                    foreach (var incrementor in node.Incrementors) Visit(incrementor);
                },
                registerLoopContext: (endLabel, startLabel) => _loopContexts.Push((endLabel, startLabel)),
                popLoopContext: () => _loopContexts.Pop());
        }

        public override void VisitForEachStatement(ForEachStatementSyntax node)
        {
            string startLabel = context.NextLabel("FOREACH_START");
            string endLabel = context.NextLabel("FOREACH_END");
            string incLabel = context.NextLabel("FOREACH_INC");

            _loopContexts.Push((endLabel, incLabel));

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

            Visit(node.Statement);

            context.MarkLabel(incLabel);

            if (isArray)
            {
                ASMInstructions.EmitOpWithImmediate(SyntaxKind.AddExpression, indexReg, indexReg, 1, context);
            }

            ASMInstructions.EmitJump(startLabel, context);

            context.MarkLabel(endLabel);

            _loopContexts.Pop();

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

        public override void VisitTryStatement(TryStatementSyntax node)
        {
            context.Emit("@ TRY BLOCK START");

            string catchLabel = context.NextLabel("CATCH_HANDLER");
            string endLabel = context.NextLabel("TRY_END");

            // Помещаем адрес обработчика в r0 и вызываем NETMCU_TryPush
            context.Emit($"ADR.W r0, {catchLabel}");
            context.AddLoadAddress(catchLabel);
            context.Write32(0xF2AF0000); // Placeholder for ADR.W r0, label

            ASMInstructions.EmitCall("NETMCU_TryPush", context, isStatic: true, isNative: true);

            Visit(node.Block);

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
            foreach (var catchClause in node.Catches)
            {
                context.Emit($"@ CATCH BLOCK: {catchClause.Declaration?.Type?.ToString()}");
                Visit(catchClause.Block);
            }

            // Возврат в throw! (как и просил пользователь, без сложной раскрутки)
            context.Emit($"B {endLabel}");
            context.AddJump(endLabel, false);
            context.Write16(0xE000); // branch empty

            context.MarkLabel(endLabel);

            if (node.Finally != null)
            {
                context.Emit("@ FINALLY BLOCK");
                Visit(node.Finally.Block);
            }

            context.Emit("@ TRY BLOCK END");
        }

        public override void VisitThrowStatement(ThrowStatementSyntax node)
        {
            context.Emit("@ THROW EXECUTION");
            if (node.Expression != null)
            {
                // Помещаем результат выражения сразу в r0 (первый аргумент для вызова)
                ASMInstructions.EmitExpression(node.Expression, 0, context);
            }
            else
            {
                context.Emit("mov r0, #0 @ Rethrow or null exception");
                ASMInstructions.EmitMovImmediate(0, 0, context);
            }

            ASMInstructions.EmitCall("NETMCU_Throw", context, isStatic: true, isNative: true);
        }

        public override void VisitWhileStatement(WhileStatementSyntax node)
        {
            context.Class.Global.Backend.GenerateWhileStatement(context, node.Condition, 
                generateBody: () => Visit(node.Statement),
                registerLoopContext: (endLabel, startLabel) => _loopContexts.Push((endLabel, startLabel)),
                popLoopContext: () => _loopContexts.Pop());
        }

        public override void VisitDoStatement(DoStatementSyntax node)
        {
            context.Class.Global.Backend.GenerateDoStatement(context, node.Condition,
                generateBody: () => Visit(node.Statement),
                registerLoopContext: (endLabel, startLabel) => _loopContexts.Push((endLabel, startLabel)),
                popLoopContext: () => _loopContexts.Pop());
        }

        public override void VisitReturnStatement(ReturnStatementSyntax node)
        {
            if (node.Expression != null)
            {
                // Результат возврата должен лечь в R0
                ASMInstructions.EmitExpression(node.Expression, 0, context);
            }

            // Прыгаем в эпилог текущего метода (метода, который будет генерировать POP {pc})
            string methodName = context.Name;
            ASMInstructions.EmitJump($"{methodName}_exit", context);
        }

        public override void VisitSwitchStatement(SwitchStatementSyntax node)
        {
            int switchReg = context.NextFreeRegister++;
            ASMInstructions.EmitExpression(node.Expression, switchReg, context);

            string endSwitchLabel = context.NextLabel("SWITCH_END");

            // В C# break внутри switch должен выйти из switch.
            _loopContexts.Push((endSwitchLabel, "ERROR_CONTINUE_IN_SWITCH"));

            int tmpReg = context.NextFreeRegister++;
            string defaultLabel = null;

            var sectionLabels = new Dictionary<SwitchSectionSyntax, string>();

            // 1. Создаем метки для блоков и генерируем проверки кейсов
            foreach (var section in node.Sections)
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
            foreach (var section in node.Sections)
            {
                context.MarkLabel(sectionLabels[section]);
                foreach (var statement in section.Statements)
                {
                    Visit(statement);
                }
            }

            // Конец
            context.MarkLabel(endSwitchLabel);
            _loopContexts.Pop();

            // Освобождаем регистры
            context.NextFreeRegister -= 2; 
        }

        public override void VisitBreakStatement(BreakStatementSyntax node)
        {
            if (_loopContexts.Count == 0) throw new Exception("Оператор break вне цикла");
            ASMInstructions.EmitJump(_loopContexts.Peek().breakLabel, context);
        }

        public override void VisitContinueStatement(ContinueStatementSyntax node)
        {
            if (_loopContexts.Count == 0) throw new Exception("Оператор continue вне цикла");
            ASMInstructions.EmitJump(_loopContexts.Peek().continueLabel, context);
        }
        public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            ASMInstructions.EmitExpression(node, 0, context);
        }

        public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
        {
            ASMInstructions.EmitExpression(node, 0, context);
        }

        public override void VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            base.VisitExpressionStatement(node);
        }
        public override void VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            ASMInstructions.EmitExpression(node, 0, context);
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            ASMInstructions.EmitExpression(node, 0, context);
        }

        private void HandleStructAssignment(AssignmentExpressionSyntax node, MemberAccessExpressionSyntax memberAccess, int srcReg)
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol;
            if (symbolInfo is IFieldSymbol fieldSymbol)
            {
                string fieldName = fieldSymbol.Name;
                string typeName = fieldSymbol.ContainingType.ToDisplayString();

                int fieldOffset = -1;

                if (context.Class.Global.Childs.TryGetValue(typeName, out var typeCtx) && typeCtx is TypeCompilationContext tcc)
                {
                    if (tcc.FieldOffsets.TryGetValue(fieldName, out int offset))
                    {
                        fieldOffset = offset;
                    }
                }
                else
                {
                    // Fallback for external types
                    int currentOffset = fieldSymbol.ContainingType.IsReferenceType && context.Class.Global.BuildingContext.Options?.TypeHeader == true ? 4 : 0;
                    foreach (var f in fieldSymbol.ContainingType.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    {
                        var typeInfo = f.Type;
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
                                fieldSize = System.Math.Max(1, typeInfo.GetMembers().OfType<IFieldSymbol>().Where(m => !m.IsStatic).Count() * 4); // basic struct size estimation
                            }
                        }

                        currentOffset = (currentOffset + align - 1) & ~(align - 1);
                        if (f.Name == fieldName)
                        {
                            fieldOffset = currentOffset;
                            break;
                        }
                        currentOffset += fieldSize;
                    }
                }

                if (fieldOffset >= 0)
                {
                    string structName = memberAccess.Expression.ToString();

                    if (context.StackMap.TryGetValue(structName, out var stackVar))
                    {
                        ASMInstructions.EmitMemoryAccess(false, srcReg, 13, stackVar.StackOffset + fieldOffset, context);
                    }
                    else
                    {
                        int baseReg = context.NextFreeRegister++;
                        ASMInstructions.EmitExpression(memberAccess.Expression, baseReg, context, 0);
                        ASMInstructions.EmitMemoryAccess(false, srcReg, baseReg, fieldOffset, context);
                        context.NextFreeRegister--;
                    }
                }
                else
                {
                    throw new Exception($"Field {fieldName} offset could not be calculated for type {typeName}");
                }
            }
        }
        private void HandleLocalAssignment(AssignmentExpressionSyntax node, int destReg, int srcReg)
        {
            bool isRef = false;
            if (node.Left is IdentifierNameSyntax id)
            {
                var symbol = context.SemanticModel.GetSymbolInfo(id).Symbol;
                if (symbol is IParameterSymbol paramSymbol && 
                    (paramSymbol.RefKind == RefKind.Ref || paramSymbol.RefKind == RefKind.Out))
                {
                    isRef = true;
                }
            }

            if (node.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                // Обычное x = y;
                if (isRef)
                {
                    // destReg contains a pointer, write srcReg to it
                    ASMInstructions.EmitMemoryAccess(false, srcReg, destReg, 0, context);
                }
                else if (destReg != srcReg)
                {
                    ASMInstructions.EmitMovRegister(destReg, srcReg, context);
                }
            }
            else
            {
                // Составное присваивание: x |= y, x += y и т.д.
                // Используем корректные SyntaxKind из Roslyn
                SyntaxKind opKind = node.Kind() switch
                {
                    SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
                    SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
                    SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression, // Исправлено
                    SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,   // Исправлено
                    _ => SyntaxKind.None
                };

                if (opKind != SyntaxKind.None)
                {
                    if (isRef)
                    {
                        int tmpReg = context.NextFreeRegister++;
                        ASMInstructions.EmitMemoryAccess(true, tmpReg, destReg, 0, context); // Load from pointer
                        ASMInstructions.EmitArithmeticOp(opKind, tmpReg, tmpReg, srcReg, context);
                        ASMInstructions.EmitMemoryAccess(false, tmpReg, destReg, 0, context); // Store to pointer
                        context.NextFreeRegister--;
                    }
                    else
                    {
                        // Выполняем операцию: Rdest = Rdest op Rsrc
                        ASMInstructions.EmitArithmeticOp(opKind, destReg, destReg, srcReg, context);
                    }
                }
            }
        }
        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            if (node.Left is ElementAccessExpressionSyntax elementAccess)
            {
                int destAddrReg = context.NextFreeRegister++;
                ASMInstructions.EmitAddressOf(elementAccess, destAddrReg, context, context.NextFreeRegister);

                int valueReg = 0;
                ASMInstructions.EmitExpression(node.Right, valueReg, context);

                var arrayTypeSymbol = context.SemanticModel.GetTypeInfo(elementAccess.Expression).Type as IArrayTypeSymbol;
                var elType = arrayTypeSymbol?.ElementType;

                int elementSize = 4; // Assume 4 bytes for now
                if (elType != null)
                {
                    if (elType.SpecialType == SpecialType.System_Byte || elType.SpecialType == SpecialType.System_SByte || elType.SpecialType == SpecialType.System_Boolean) elementSize = 1;
                    else if (elType.SpecialType == SpecialType.System_Int16 || elType.SpecialType == SpecialType.System_UInt16 || elType.SpecialType == SpecialType.System_Char) elementSize = 2;
                    else if (elType.SpecialType == SpecialType.System_Int64 || elType.SpecialType == SpecialType.System_UInt64 || elType.SpecialType == SpecialType.System_Double) elementSize = 8;
                    else if (elType.TypeKind == TypeKind.Struct)
                        elementSize = System.Math.Max(1, elType.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Count() * 4);
                }

                if (node.IsKind(SyntaxKind.SimpleAssignmentExpression))
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
                else
                {
                    int tmpRead = context.NextFreeRegister++;
                    if (elementSize == 1)
                    {
                        context.Emit($"LDRB r{tmpRead}, [r{destAddrReg}, #0]");
                        context.Write16((ushort)(0x7800 | ((destAddrReg & 0x7) << 3) | (tmpRead & 0x7)));
                    }
                    else if (elementSize == 2)
                    {
                        context.Emit($"LDRH r{tmpRead}, [r{destAddrReg}, #0]");
                        context.Write16((ushort)(0x8800 | ((destAddrReg & 0x7) << 3) | (tmpRead & 0x7)));
                    }
                    else
                    {
                        context.Emit($"LDR r{tmpRead}, [r{destAddrReg}, #0]");
                        context.Write16((ushort)(0x6800 | ((destAddrReg & 0x7) << 3) | (tmpRead & 0x7)));
                    }

                    SyntaxKind opKind = node.Kind() switch
                    {
                        SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
                        SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
                        SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
                        SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
                        _ => SyntaxKind.None
                    };

                    if (opKind != SyntaxKind.None)
                    {
                        ASMInstructions.EmitArithmeticOp(opKind, tmpRead, tmpRead, valueReg, context);
                        if (elementSize == 1)
                        {
                            context.Emit($"STRB r{tmpRead}, [r{destAddrReg}, #0]");
                            context.Write16((ushort)(0x7000 | ((destAddrReg & 0x7) << 3) | (tmpRead & 0x7)));
                        }
                        else if (elementSize == 2)
                        {
                            context.Emit($"STRH r{tmpRead}, [r{destAddrReg}, #0]");
                            context.Write16((ushort)(0x8000 | ((destAddrReg & 0x7) << 3) | (tmpRead & 0x7)));
                        }
                        else
                        {
                            context.Emit($"STR r{tmpRead}, [r{destAddrReg}, #0]");
                            context.Write16((ushort)(0x6000 | ((destAddrReg & 0x7) << 3) | (tmpRead & 0x7)));
                        }
                    }
                    context.NextFreeRegister--;
                }

                context.NextFreeRegister--;
                return;
            }

            int standardValueReg = 0;

            ASMInstructions.EmitExpression(node.Right, standardValueReg, context);

            if (node.Left is MemberAccessExpressionSyntax memberAccess)
            {
                var symbolInfo = context.SemanticModel.GetSymbolInfo(node.Left).Symbol;
                if (symbolInfo is IPropertySymbol propertySymbol && propertySymbol.SetMethod != null)
                {
                    // Нужно вызвать set_Property(value)

                    int regOffset = 0;
                    if (!propertySymbol.IsStatic)
                    {
                        // "this" вычисляем и кладем в R0
                        ASMInstructions.EmitExpression(memberAccess.Expression, 0, context);
                        regOffset = 1;
                    }

                    // Значение кладем в R1 (или R0, если статическое)
                    if (standardValueReg != regOffset)
                    {
                        ASMInstructions.EmitMovRegister(regOffset, standardValueReg, context);
                    }

                    string nativeFunctionName = propertySymbol.SetMethod.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.Name.Contains("NativeCall") == true)?
                        .ConstructorArguments.FirstOrDefault().Value?.ToString();

                    string callTarget = nativeFunctionName ?? propertySymbol.SetMethod.ToDisplayString();
                    ASMInstructions.EmitCall(callTarget, context, propertySymbol.IsStatic, nativeFunctionName != null);
                }
                else
                {
                    HandleStructAssignment(node, memberAccess, standardValueReg);
                }
            }
            else
            {
                string varName = node.Left.ToString();
                if (context.RegisterMap.TryGetValue(varName, out int destReg))
                {
                    HandleLocalAssignment(node, destReg, standardValueReg);
                }
                else if (context.StackMap.TryGetValue(varName, out var stackVar))
                {
                    if (node.IsKind(SyntaxKind.SimpleAssignmentExpression))
                    {
                        ASMInstructions.EmitMemoryAccess(false, standardValueReg, 13, stackVar.StackOffset, context);
                    }
                    else
                    {
                        SyntaxKind opKind = node.Kind() switch
                        {
                            SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
                            SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
                            SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
                            SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
                            _ => SyntaxKind.None
                        };

                        if (opKind != SyntaxKind.None)
                        {
                            int tmpReg = context.NextFreeRegister++;
                            ASMInstructions.EmitMemoryAccess(true, tmpReg, 13, stackVar.StackOffset, context);
                            ASMInstructions.EmitArithmeticOp(opKind, tmpReg, tmpReg, standardValueReg, context);
                            ASMInstructions.EmitMemoryAccess(false, tmpReg, 13, stackVar.StackOffset, context);
                            context.NextFreeRegister--;
                        }
                    }
                }
                else
                {
                    throw new Exception($"Переменная {varName} не объявлена");
                }
            }
        }

        public override void VisitIfStatement(IfStatementSyntax node)
        {
            context.Class.Global.Backend.GenerateIfStatement(context, node.Condition, 
                generateTrueBlock: () => Visit(node.Statement),
                generateFalseBlock: node.Else != null ? () => Visit(node.Else.Statement) : null);
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            // Метод может не возвращать значение, или мы игнорируем его
            // Обработчик в EmitExpression уже знает как сгенерировать BL и подготовить аргументы
            ASMInstructions.EmitExpression(node, 0, context);
        }

    }
}
