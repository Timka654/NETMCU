using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Linq;
using System.Collections.Immutable;

namespace NETMCUCompiler.CodeBuilder.Backends
{
    public record VariableAllocationContext(string VarName, int StackOffset, int RegisterIndex, bool IsStack, bool HasInitializer, int InitValueReg);

    public abstract class MCUBackend
    {
        public abstract TypeCompilationContext CreateTypeContext(TypeDeclarationSyntax type, SemanticModel semanticModel, CompilationContext global, string name);
        public abstract MethodCompilationContext CreateMethodContext(SyntaxNode methodSyntax, IMethodSymbol symbol, BaseCompilationContext parentContext, string name);

        public abstract void GenerateMethodPrologue(MethodCompilationContext context, bool isInstance, ImmutableArray<IParameterSymbol> parameters);
        public abstract void GenerateMethodEpilogue(MethodCompilationContext context);

        public virtual void GenerateIfStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateTrueBlock, Action generateFalseBlock)
        {
            string trueLabel = $"L_TRUE_{context.LabelCount++}";
            string falseLabel = $"L_FALSE_{context.LabelCount++}";
            string endLabel = $"L_END_{context.LabelCount++}";

            EmitLogicalCondition(context, condition, trueLabel, falseLabel);

            context.MarkLabel(trueLabel);
            generateTrueBlock();
            EmitJump(context, endLabel);

            context.MarkLabel(falseLabel);
            if (generateFalseBlock != null)
            {
                generateFalseBlock();
            }

            context.MarkLabel(endLabel);
        }

        public virtual void GenerateWhileStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateBody, Action<string, string> registerLoopContext, Action popLoopContext)
        {
            string startLabel = context.NextLabel("WHILE_START");
            string endLabel = context.NextLabel("WHILE_END");

            registerLoopContext(endLabel, startLabel);
            context.MarkLabel(startLabel);

            EmitLogicalCondition(context, condition, "", endLabel);

            generateBody();

            EmitJump(context, startLabel);
            context.MarkLabel(endLabel);
            popLoopContext();
        }

        public virtual void GenerateDoStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateBody, Action<string, string> registerLoopContext, Action popLoopContext)
        {
            string startLabel = context.NextLabel("DO_START");
            string endLabel = context.NextLabel("DO_END");
            string condLabel = context.NextLabel("DO_COND");

            registerLoopContext(endLabel, condLabel);
            context.MarkLabel(startLabel);

            generateBody();

            context.MarkLabel(condLabel);
            EmitLogicalCondition(context, condition, startLabel, endLabel);

            context.MarkLabel(endLabel);
            popLoopContext();
        }

        public virtual void GenerateForStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateInit, Action generateBody, Action generateIncrementor, Action<string, string> registerLoopContext, Action popLoopContext)
        {
            generateInit?.Invoke();

            string startLabel = context.NextLabel("FOR_START");
            string endLabel = context.NextLabel("FOR_END");
            string incLabel = context.NextLabel("FOR_INC");

            registerLoopContext(endLabel, incLabel);
            context.MarkLabel(startLabel);

            if (condition != null)
            {
                EmitLogicalCondition(context, condition, "", endLabel);
            }

            generateBody?.Invoke();

            context.MarkLabel(incLabel);
            generateIncrementor?.Invoke();

            EmitJump(context, startLabel);
            context.MarkLabel(endLabel);

            popLoopContext();
        }

        public virtual void GenerateForEachStatement(MethodCompilationContext context, ForEachStatementSyntax node, Action generateBody, Action<string, string> registerLoopContext, Action popLoopContext)
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
                EmitExpressionValue(context, node.Expression, collectionReg);

                indexReg = context.NextFreeRegister++;
                EmitMovImmediate(context, indexReg, 0);

                lengthReg = context.NextFreeRegister++;

                int fourReg = context.NextFreeRegister++;
                EmitMovImmediate(context, fourReg, 4);

                int lenAddrReg = context.NextFreeRegister++;
                EmitArithmeticOp(context, SyntaxKind.SubtractExpression, lenAddrReg, collectionReg, fourReg);
                context.Emit($"@ Read array length");
                EmitMemoryAccess(context, true, lengthReg, lenAddrReg, 0);
                context.NextFreeRegister -= 2;

                context.MarkLabel(startLabel);

                EmitCompare(context, indexReg, lengthReg);
                EmitBranch(context, endLabel, "GE");

                int offsetReg = context.NextFreeRegister++;
                EmitMovRegister(context, offsetReg, indexReg);
                int elemSizeReg = context.NextFreeRegister++;
                EmitMovImmediate(context, elemSizeReg, 4);
                EmitArithmeticOp(context, SyntaxKind.MultiplyExpression, offsetReg, offsetReg, elemSizeReg);

                int targetAddrReg = context.NextFreeRegister++;
                EmitMovRegister(context, targetAddrReg, collectionReg);
                EmitArithmeticOp(context, SyntaxKind.AddExpression, targetAddrReg, targetAddrReg, offsetReg);

                EmitMemoryAccess(context, true, itemReg, targetAddrReg, 0);
                context.NextFreeRegister -= 3;
            }
            else
            {
                var foreachInfo = context.SemanticModel.GetForEachStatementInfo(node);
                if (foreachInfo.GetEnumeratorMethod == null || foreachInfo.MoveNextMethod == null || foreachInfo.CurrentProperty == null)
                    throw new Exception("Не удалось разрешить GetEnumerator, MoveNext или Current для foreach");

                EmitExpressionValue(context, node.Expression, 0);

                string getEnumTarget = foreachInfo.GetEnumeratorMethod.ToDisplayString();
                EmitCall(context, getEnumTarget, foreachInfo.GetEnumeratorMethod.IsStatic);

                enumReg = context.NextFreeRegister++;
                EmitMovRegister(context, enumReg, 0);

                context.MarkLabel(startLabel);

                EmitMovRegister(context, 0, enumReg);
                string moveNextTarget = foreachInfo.MoveNextMethod.ToDisplayString();
                EmitCall(context, moveNextTarget, foreachInfo.MoveNextMethod.IsStatic);

                EmitCompareImmediate(context, 0, 0);
                EmitBranch(context, endLabel, "EQ");

                EmitMovRegister(context, 0, enumReg);
                string currentTarget = foreachInfo.CurrentProperty.GetMethod.ToDisplayString();
                EmitCall(context, currentTarget, foreachInfo.CurrentProperty.IsStatic);

                EmitMovRegister(context, itemReg, 0);
            }

            generateBody();

            context.MarkLabel(incLabel);

            if (isArray)
            {
                EmitOpWithImmediate(context, SyntaxKind.AddExpression, indexReg, indexReg, 1);
            }

            EmitJump(context, startLabel);

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

        public virtual void GenerateBreakStatement(MethodCompilationContext context, string breakLabel)
        {
            EmitJump(context, breakLabel);
        }

        public virtual void GenerateContinueStatement(MethodCompilationContext context, string continueLabel)
        {
            EmitJump(context, continueLabel);
        }

        public abstract void GenerateTryStatement(MethodCompilationContext context, Action generateTryBlock, Action<CatchClauseSyntax> generateCatchBlock, Action generateFinallyBlock, SyntaxList<CatchClauseSyntax> catches, FinallyClauseSyntax finallyClause);

        public virtual void GenerateThrowStatement(MethodCompilationContext context, ExpressionSyntax expression)
        {
            context.Emit("@ THROW EXECUTION");
            if (expression != null)
            {
                // Помещаем результат выражения сразу в r0 (первый аргумент для вызова)
                EmitExpressionValue(context, expression, 0);
            }
            else
            {
                context.Emit("mov r0, #0 @ Rethrow or null exception");
                EmitMovImmediate(context, 0, 0);
            }

            EmitCall(context, "NETMCU_Throw", isStatic: true, isNative: true);
        }

        public virtual void GenerateReturnStatement(MethodCompilationContext context, ExpressionSyntax expression)
        {
            if (expression != null)
            {
                // Результат возврата должен лечь в R0
                EmitExpressionValue(context, expression, 0);
            }

            // Прыгаем в эпилог текущего метода (метода, который будет генерировать POP {pc})
            string methodName = context.Name;
            EmitJump(context, $"{methodName}_exit");
        }

        public virtual void GenerateSwitchStatement(MethodCompilationContext context, ExpressionSyntax expression, SyntaxList<SwitchSectionSyntax> sections, Action<SwitchSectionSyntax> generateSectionBody, Action<string, string> registerLoopContext, Action popLoopContext)
        {
            int switchReg = context.NextFreeRegister++;
            EmitExpressionValue(context, expression, switchReg);

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
                        EmitExpressionValue(context, caseLabel.Value, tmpReg);
                        EmitCompare(context, switchReg, tmpReg);
                        EmitBranch(context, sectionLabel, "EQ");
                    }
                }
            }

            // 2. Если ни один не подошел, и есть default:
            if (defaultLabel != null)
            {
                EmitJump(context, defaultLabel);
            }
            else
            {
                // Если нет default, прыгаем в конец
                EmitJump(context, endSwitchLabel);
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

        public virtual void GenerateVariableDeclaration(MethodCompilationContext context, VariableDeclarationSyntax declaration)
        {
            foreach (var variable in declaration.Variables)
            {
                string varName = variable.Identifier.Text;
                bool isStack = context.StackMap.TryGetValue(varName, out var stackVar);
                int stackOffset = isStack ? stackVar.StackOffset : -1;
                int registerIndex = -1;

                if (!isStack)
                {
                    if (context.RegisterMap.TryGetValue(varName, out int existingReg))
                    {
                        registerIndex = existingReg;
                    }
                    else
                    {
                        if (context.NextFreeRegister > 11) throw new System.Exception("Закончились регистры r4-r11");
                        registerIndex = context.NextFreeRegister++;
                        context.RegisterMap[varName] = registerIndex;
                    }
                }

                bool hasInitializer = variable.Initializer != null;
                int initValueReg = registerIndex;

                if (hasInitializer)
                {
                    if (isStack) 
                    {
                        initValueReg = context.NextFreeRegister++;
                    }

                    EmitExpressionValue(context, variable.Initializer.Value, initValueReg);
                }

                EmitVariableAllocation(context, new VariableAllocationContext(varName, stackOffset, registerIndex, isStack, hasInitializer, initValueReg));

                if (hasInitializer && isStack)
                {
                    context.NextFreeRegister--;
                }
            }
        }

        public abstract void EmitVariableAllocation(MethodCompilationContext context, VariableAllocationContext allocContext);

        public abstract void EmitStoreToArrayElement(MethodCompilationContext context, int elementSize, int valueReg, int destAddrReg);
        public abstract void EmitLoadFromArrayElement(MethodCompilationContext context, int elementSize, int resultReg, int sourceAddrReg);

        protected virtual void HandleStructAssignment(MethodCompilationContext context, AssignmentExpressionSyntax node, MemberAccessExpressionSyntax memberAccess, int srcReg)
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
                        EmitMemoryAccess(context, false, srcReg, 13, stackVar.StackOffset + fieldOffset);
                    }
                    else
                    {
                        int baseReg = context.NextFreeRegister++;
                        EmitExpressionValue(context, memberAccess.Expression, baseReg);
                        EmitMemoryAccess(context, false, srcReg, baseReg, fieldOffset);
                        context.NextFreeRegister--;
                    }
                }
                else
                {
                    throw new Exception($"Field {fieldName} offset could not be calculated for type {typeName}");
                }
            }
        }

        protected virtual void HandleLocalAssignment(MethodCompilationContext context, AssignmentExpressionSyntax node, int destReg, int srcReg)
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
                    EmitMemoryAccess(context, false, srcReg, destReg, 0);
                }
                else if (destReg != srcReg)
                {
                    EmitMovRegister(context, destReg, srcReg);
                }
            }
            else
            {
                // Составное присваивание: x |= y, x += y и т.д.
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
                    if (isRef)
                    {
                        int tmpReg = context.NextFreeRegister++;
                        EmitMemoryAccess(context, true, tmpReg, destReg, 0); // Load from pointer
                        EmitArithmeticOp(context, opKind, tmpReg, tmpReg, srcReg);
                        EmitMemoryAccess(context, false, tmpReg, destReg, 0); // Store to pointer
                        context.NextFreeRegister--;
                    }
                    else
                    {
                        // Выполняем операцию: Rdest = Rdest op Rsrc
                        EmitArithmeticOp(context, opKind, destReg, destReg, srcReg);
                    }
                }
            }
        }

        public virtual void GenerateAssignmentExpression(MethodCompilationContext context, AssignmentExpressionSyntax node)
        {
            if (node.Left is ElementAccessExpressionSyntax elementAccess)
            {
                int destAddrReg = context.NextFreeRegister++;
                EmitAddressOf(context, elementAccess, destAddrReg, context.NextFreeRegister);

                int valueReg = 0;
                EmitExpressionValue(context, node.Right, valueReg);

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
                    EmitStoreToArrayElement(context, elementSize, valueReg, destAddrReg);
                }
                else
                {
                    int tmpRead = context.NextFreeRegister++;
                    EmitLoadFromArrayElement(context, elementSize, tmpRead, destAddrReg);

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
                        EmitArithmeticOp(context, opKind, tmpRead, tmpRead, valueReg);
                        EmitStoreToArrayElement(context, elementSize, tmpRead, destAddrReg);
                    }
                    context.NextFreeRegister--;
                }

                context.NextFreeRegister--;
                return;
            }

            int standardValueReg = 0;

            EmitExpressionValue(context, node.Right, standardValueReg);

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
                        EmitExpressionValue(context, memberAccess.Expression, 0);
                        regOffset = 1;
                    }

                    // Значение кладем в R1 (или R0, если статическое)
                    if (standardValueReg != regOffset)
                    {
                        EmitMovRegister(context, regOffset, standardValueReg);
                    }

                    string nativeFunctionName = propertySymbol.SetMethod.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.Name.Contains("NativeCall") == true)?
                        .ConstructorArguments.FirstOrDefault().Value?.ToString();

                    string callTarget = nativeFunctionName ?? propertySymbol.SetMethod.ToDisplayString();
                    EmitCall(context, callTarget, propertySymbol.IsStatic, nativeFunctionName != null);
                }
                else
                {
                    HandleStructAssignment(context, node, memberAccess, standardValueReg);
                }
            }
            else
            {
                string varName = node.Left.ToString();
                if (context.RegisterMap.TryGetValue(varName, out int destReg))
                {
                    HandleLocalAssignment(context, node, destReg, standardValueReg);
                }
                else if (context.StackMap.TryGetValue(varName, out var stackVar))
                {
                    if (node.IsKind(SyntaxKind.SimpleAssignmentExpression))
                    {
                        EmitMemoryAccess(context, false, standardValueReg, 13, stackVar.StackOffset);
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
                            EmitMemoryAccess(context, true, tmpReg, 13, stackVar.StackOffset);
                            EmitArithmeticOp(context, opKind, tmpReg, tmpReg, standardValueReg);
                            EmitMemoryAccess(context, false, tmpReg, 13, stackVar.StackOffset);
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

        public virtual void GeneratePrefixUnaryExpression(MethodCompilationContext context, PrefixUnaryExpressionSyntax node)
        {
            EmitExpressionValue(context, node, 0);
        }

        public virtual void GeneratePostfixUnaryExpression(MethodCompilationContext context, PostfixUnaryExpressionSyntax node)
        {
            EmitExpressionValue(context, node, 0);
        }

        public virtual void GenerateLiteralExpression(MethodCompilationContext context, LiteralExpressionSyntax node)
        {
            EmitExpressionValue(context, node, 0);
        }

        public virtual void GenerateIdentifierName(MethodCompilationContext context, IdentifierNameSyntax node)
        {
            EmitExpressionValue(context, node, 0);
        }

        public virtual void GenerateInvocationExpression(MethodCompilationContext context, InvocationExpressionSyntax node)
        {
            EmitExpressionValue(context, node, 0);
        }

        public abstract void EmitJump(MethodCompilationContext context, string label);
        
        public virtual void EmitLogicalCondition(MethodCompilationContext context, ExpressionSyntax condition, string trueLabel, string falseLabel)
        {
            if (condition is ParenthesizedExpressionSyntax paren)
            {
                EmitLogicalCondition(context, paren.Expression, trueLabel, falseLabel);
                return;
            }

            if (condition is BinaryExpressionSyntax bin)
            {
                if (bin.IsKind(SyntaxKind.LogicalOrExpression))
                {
                    EmitLogicalCondition(context, bin.Left, trueLabel, "");
                    EmitLogicalCondition(context, bin.Right, trueLabel, falseLabel);
                    return;
                }
                if (bin.IsKind(SyntaxKind.LogicalAndExpression))
                {
                    string nextAnd = $"L_AND_{context.LabelCount++}";
                    EmitLogicalCondition(context, bin.Left, nextAnd, falseLabel);
                    context.MarkLabel(nextAnd);
                    EmitLogicalCondition(context, bin.Right, trueLabel, falseLabel);
                    return;
                }

                if (bin.IsKind(SyntaxKind.EqualsExpression) || bin.IsKind(SyntaxKind.NotEqualsExpression) ||
                    bin.IsKind(SyntaxKind.GreaterThanExpression) || bin.IsKind(SyntaxKind.LessThanExpression) ||
                    bin.IsKind(SyntaxKind.GreaterThanOrEqualExpression) || bin.IsKind(SyntaxKind.LessThanOrEqualExpression))
                {
                    int startReg = context.NextFreeRegister;
                    int leftReg = context.NextFreeRegister++;
                    EmitExpressionValue(context, bin.Left, leftReg);

                    int rightReg = context.NextFreeRegister++;
                    EmitExpressionValue(context, bin.Right, rightReg);

                    EmitCompare(context, leftReg, rightReg);

                    string op = bin.Kind() switch
                    {
                        SyntaxKind.EqualsExpression => "EQ",
                        SyntaxKind.NotEqualsExpression => "NE",
                        SyntaxKind.GreaterThanExpression => "GT",
                        SyntaxKind.LessThanExpression => "LT",
                        SyntaxKind.GreaterThanOrEqualExpression => "GE",
                        SyntaxKind.LessThanOrEqualExpression => "LE",
                        _ => "EQ"
                    };

                    if (!string.IsNullOrEmpty(trueLabel))
                        EmitBranch(context, trueLabel, op);

                    if (!string.IsNullOrEmpty(falseLabel))
                    {
                        string invOp = op switch { "EQ" => "NE", "NE" => "EQ", "GT" => "LE", "LT" => "GE", "GE" => "LT", "LE" => "GT", _ => "NE" };
                        EmitBranch(context, falseLabel, invOp);
                    }

                    context.NextFreeRegister = startReg;
                    return;
                }
            }

            if (condition is PrefixUnaryExpressionSyntax unary && unary.IsKind(SyntaxKind.LogicalNotExpression))
            {
                int startReg = context.NextFreeRegister;
                int reg = context.NextFreeRegister++;
                EmitExpressionValue(context, unary.Operand, reg);
                EmitCompareImmediate(context, reg, 0);

                if (!string.IsNullOrEmpty(trueLabel))
                    EmitBranch(context, trueLabel, "EQ");
                if (!string.IsNullOrEmpty(falseLabel))
                    EmitBranch(context, falseLabel, "NE");

                context.NextFreeRegister = startReg;
                return;
            }

            // Fallback: evaluate anything as boolean expression directly
            int condStartReg = context.NextFreeRegister;
            int condReg = context.NextFreeRegister++;
            EmitExpressionValue(context, condition, condReg);
            EmitCompareImmediate(context, condReg, 0);

            if (!string.IsNullOrEmpty(trueLabel))
                EmitBranch(context, trueLabel, "NE");
            if (!string.IsNullOrEmpty(falseLabel))
                EmitBranch(context, falseLabel, "EQ");

            context.NextFreeRegister = condStartReg;
        }

        public virtual int ParseLiteral(CompilationContext globalContext, LiteralExpressionSyntax literal)
        {
            if (literal.Token.ValueText == "true") return 1;
            if (literal.Token.ValueText == "false") return 0;
            if (literal.Token.Value == null) return 0;

            if (literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                if (globalContext != null)
                {
                    var strValue = literal.Token.ValueText;
                    var label = globalContext.RegisterStringLiteral(strValue);
                    return 0;
                }
                return 0;
            }

            try
            {
                return Convert.ToInt32(literal.Token.Value);
            }
            catch
            {
                return 0;
            }
        }

        public virtual bool TryGetAsConstant(MethodCompilationContext context, ExpressionSyntax expr, out object value)
        {
            value = 0;
            if (expr is LiteralExpressionSyntax literal) { value = ParseLiteral(context.Class.Global, literal); return true; }

            if (expr is IdentifierNameSyntax id && context.TryGetConstant(context.SemanticModel.GetSymbolInfo(expr).Symbol.ToDisplayString(), out value))
                return true;

            if (expr is MemberAccessExpressionSyntax ma && context.TryGetConstant(context.SemanticModel.GetSymbolInfo(expr).Symbol.ToDisplayString(), out value))
                return true;

            return false;
        }

        public abstract void EmitExpressionValue(MethodCompilationContext context, ExpressionSyntax expr, int targetReg);
        public abstract void EmitCall(MethodCompilationContext context, string name, bool isStatic, bool isNative = false);
        public abstract void EmitMovImmediate(MethodCompilationContext context, int reg, int val);
        public abstract void EmitCompare(MethodCompilationContext context, int left, int right);
        public abstract void EmitBranch(MethodCompilationContext context, string label, string condition);
        public abstract void EmitMovRegister(MethodCompilationContext context, int target, int source);
        public abstract void EmitMemoryAccess(MethodCompilationContext context, bool isLoad, int targetReg, int baseReg, int offset);
        public abstract void EmitArithmeticOp(MethodCompilationContext context, SyntaxKind op, int target, int left, int right);
        public abstract void EmitOpWithImmediate(MethodCompilationContext context, SyntaxKind op, int target, int left, int value);
        public abstract void EmitAddressOf(MethodCompilationContext context, ExpressionSyntax expr, int targetReg, int tempOffset = 0);
        public abstract void EmitCompareImmediate(MethodCompilationContext context, int reg, int imm);

        public abstract void ResolveJumps(MethodCompilationContext context);
    }
}