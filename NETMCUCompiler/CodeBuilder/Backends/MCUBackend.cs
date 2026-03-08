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
                EmitComment(context, "Read array length");
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
                    throw new Exception("�� ������� ��������� GetEnumerator, MoveNext ��� Current ��� foreach");

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
            EmitComment(context, "THROW EXECUTION");
            if (expression != null)
            {
                // �������� ��������� ��������� ����� � r0 (������ �������� ��� ������)
                EmitExpressionValue(context, expression, 0);
            }
            else
            {
                EmitComment(context, "Rethrow or null exception");
                EmitMovImmediate(context, 0, 0);
            }

            EmitCall(context, "NETMCU_Throw", isStatic: true, isNative: true);
        }

        public virtual void GenerateReturnStatement(MethodCompilationContext context, ExpressionSyntax expression)
        {
            if (expression != null)
            {
                // ��������� �������� ������ ���� � R0
                EmitExpressionValue(context, expression, 0);
            }

            // ������� � ������ �������� ������ (������, ������� ����� ������������ POP {pc})
            string methodName = context.Name;
            EmitJump(context, $"{methodName}_exit");
        }

        public virtual void GenerateSwitchStatement(MethodCompilationContext context, ExpressionSyntax expression, SyntaxList<SwitchSectionSyntax> sections, Action<SwitchSectionSyntax> generateSectionBody, Action<string, string> registerLoopContext, Action popLoopContext)
        {
            int switchReg = context.NextFreeRegister++;
            EmitExpressionValue(context, expression, switchReg);

            string endSwitchLabel = context.NextLabel("SWITCH_END");

            // � C# break ������ switch ������ ����� �� switch.
            registerLoopContext(endSwitchLabel, "ERROR_CONTINUE_IN_SWITCH");

            int tmpReg = context.NextFreeRegister++;
            string defaultLabel = null;

            var sectionLabels = new Dictionary<SwitchSectionSyntax, string>();

            // 1. ������� ����� ��� ������ � ���������� �������� ������
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
                        // ����������
                        EmitExpressionValue(context, caseLabel.Value, tmpReg);
                        EmitCompare(context, switchReg, tmpReg);
                        EmitBranch(context, sectionLabel, "EQ");
                    }
                }
            }

            // 2. ���� �� ���� �� �������, � ���� default:
            if (defaultLabel != null)
            {
                EmitJump(context, defaultLabel);
            }
            else
            {
                // ���� ��� default, ������� � �����
                EmitJump(context, endSwitchLabel);
            }

            // 3. ����� ��� ������
            foreach (var section in sections)
            {
                context.MarkLabel(sectionLabels[section]);
                generateSectionBody(section);
            }

            // �����
            context.MarkLabel(endSwitchLabel);
            popLoopContext();

            // ����������� ��������
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
                        if (context.NextFreeRegister > 11) throw new System.Exception("����������� �������� r4-r11");
                        registerIndex = context.NextFreeRegister++;
                        context.RegisterMap[varName] = registerIndex;
                    }
                }

                if (isStack)
                {
                    EmitComment(context, $"Allocation: {varName} -> Stack[{stackOffset}]");
                }
                else
                {
                    EmitComment(context, $"Allocation: {varName} -> r{registerIndex}");
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
                // ������� x = y;
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
                // ��������� ������������: x |= y, x += y � �.�.
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
                        // ��������� ��������: Rdest = Rdest op Rsrc
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
                    // ����� ������� set_Property(value)
                    int regOffset = 0;
                    if (!propertySymbol.IsStatic)
                    {
                        // "this" ��������� � ������ � R0
                        EmitExpressionValue(context, memberAccess.Expression, 0);
                        regOffset = 1;
                    }

                    // �������� ������ � R1 (��� R0, ���� �����������)
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
                    throw new Exception($"���������� {varName} �� ���������");
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

        public abstract void EmitDelegateCreation(MethodCompilationContext context, IMethodSymbol targetMethod, ExpressionSyntax nodeExpr, ITypeSymbol delegateType, int targetReg, int tempOffset);

        public virtual void EmitArithmetic(BinaryExpressionSyntax node, int targetReg, MethodCompilationContext context, int tempOffset = 0)
        {
            // Левая часть: используйте текущий свободный регистр (r0, r1...)
            int leftReg = GetOperandRegister(node.Left, context, tempOffset);

            // Правая часть: ОБЯЗАТЕЛЬНО используем СЛЕДУЮЩИЙ регистр
            int rightTemp = tempOffset + 1;

            if (node.Right is LiteralExpressionSyntax literal)
            {
                int value = ParseLiteral(context.Class.Global, literal);
                if (value >= 0 && value <= 7 && (node.IsKind(SyntaxKind.AddExpression) || node.IsKind(SyntaxKind.SubtractExpression)))
                {
                    EmitOpWithImmediate(context, node.Kind(), targetReg, leftReg, value);
                }
                else
                {
                    EmitMovImmediate(context, rightTemp, value);
                    EmitArithmeticOp(context, node.Kind(), targetReg, leftReg, rightTemp);
                }
            }
            else if (node.Right is IdentifierNameSyntax id)
            {
                // 1. Проверяем, константа ли это (d1, Program.d2)
                if (TryGetAsConstant(context, id, out object constVal))
                {
                    EmitMovImmediate(context, rightTemp, (int)constVal); // Грузим константу в r(rightTemp)
                    EmitArithmeticOp(context, node.Kind(), targetReg, leftReg, rightTemp);
                }
                // 2. Если это переменная (a, b...)
                else if (context.RegisterMap.TryGetValue(id.Identifier.Text, out int rightReg))
                {
                    EmitArithmeticOp(context, node.Kind(), targetReg, leftReg, rightReg);
                }
            }
            else
            {
                // Рекурсия: передаем rightTemp как целевой регистр И как новый оффсет
                EmitExpressionValue(context, node.Right, rightTemp, rightTemp);
                EmitArithmeticOp(context, node.Kind(), targetReg, leftReg, rightTemp);
            }
        }

        protected virtual int GetOperandRegister(ExpressionSyntax expr, MethodCompilationContext context, int tempOffset)
        {
            if (expr is IdentifierNameSyntax id && context.RegisterMap.TryGetValue(id.Identifier.Text, out int reg)) return reg;

            if (TryGetAsConstant(context, expr, out object val))
            {
                EmitMovImmediate(context, tempOffset, (int)val);
                return tempOffset;
            }

            EmitExpressionValue(context, expr, tempOffset, tempOffset + 1);
            return tempOffset;
        }

        public virtual void EmitOpWithImmediate(SyntaxKind op, int target, int left, int value, MethodCompilationContext context)
        {
            EmitOpWithImmediate(context, op, target, left, value);
        }
        public virtual void EmitArithmeticOp(SyntaxKind op, int target, int left, int right, MethodCompilationContext context)
        {
            EmitArithmeticOp(context, op, target, left, right);
        }

        public virtual void EmitMovRegister(int target, int source, MethodCompilationContext context)
        {
            EmitMovRegister(context, target, source);
        }

        public virtual void EmitLdrImmediate(int target, int offset, MethodCompilationContext context)
        {
            // Placeholder: currently not strictly needed without baseReg. Actually, let's just make EmitMemoryAccess.
        }

        public virtual void EmitAddressOf(ExpressionSyntax expr, int targetReg, MethodCompilationContext context, int tempOffset = 0)
        {
            EmitAddressOf(context, expr, targetReg, tempOffset);
        }

        public virtual void EmitMemoryAccess(bool isLoad, int targetReg, int baseReg, int offset, MethodCompilationContext context)
        {
            EmitMemoryAccess(context, isLoad, targetReg, baseReg, offset);
        }

        // Базовый MOV (Rd = Imm8)
        public virtual void EmitMovImmediate(int reg, int val, MethodCompilationContext context)
        {
            EmitMovImmediate(context, reg, val);
        }
        public virtual void EmitDivide(int target, int left, int right, MethodCompilationContext context)
        {
            EmitArithmeticOp(context, SyntaxKind.DivideExpression, target, left, right);
        }

        public virtual void EmitLogicalCondition(ExpressionSyntax condition, string trueLabel, string falseLabel, MethodCompilationContext context)
        {
            EmitLogicalCondition(context, condition, trueLabel, falseLabel);
        }
        public virtual void EmitExpressionValue(MethodCompilationContext context, ExpressionSyntax expr, int targetReg, int tempOffset = 0)
        {
            var typeInfo = context.SemanticModel.GetTypeInfo(expr);
            var conversion = context.SemanticModel.GetConversion(expr);

            if (conversion.IsBoxing)
            {
                Console.WriteLine($"[DEBUG-CONV] BOXING on {expr}");
                int valReg = context.NextFreeRegister++;
                EmitExpressionInternal(context, expr, valReg, tempOffset);

                var typeSymbol = typeInfo.Type;
                int size = 4;
                if (typeSymbol != null)
                {
                    if (typeSymbol.SpecialType == SpecialType.System_Byte || typeSymbol.SpecialType == SpecialType.System_SByte || typeSymbol.SpecialType == SpecialType.System_Boolean) size = 1;
                    else if (typeSymbol.SpecialType == SpecialType.System_Int16 || typeSymbol.SpecialType == SpecialType.System_UInt16 || typeSymbol.SpecialType == SpecialType.System_Char) size = 2;
                    else if (typeSymbol.SpecialType == SpecialType.System_Int64 || typeSymbol.SpecialType == SpecialType.System_UInt64 || typeSymbol.SpecialType == SpecialType.System_Double) size = 8;
                    else if (typeSymbol.SpecialType == SpecialType.System_Int32 || typeSymbol.SpecialType == SpecialType.System_UInt32 || typeSymbol.SpecialType == SpecialType.System_Single || typeSymbol.SpecialType == SpecialType.System_IntPtr) size = 4;
                    else if (typeSymbol.TypeKind == TypeKind.Struct)
                        size = Math.Max(1, typeSymbol.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Count() * 4);
                }

                int allocSize = size + 4; // Header + payload
                EmitMovImmediate(context, 0, allocSize);
                EmitCall(context, "NETMCU__Memory__Alloc", isStatic: true, isNative: true);

                if (typeSymbol != null && context.Class.Global.BuildingContext.Options?.TypeHeader == true)
                {
                    string targetName = typeSymbol.ToDisplayString();
                    string symbolName = context.Class.Global.RegisterTypeLiteral(typeSymbol);

                    int tmpReg = context.NextFreeRegister++;
                    EmitComment(context, $"Write TypeHeader for Boxed {targetName}");
                    EmitLoadSymbolAddress(context, tmpReg, symbolName);
                    EmitMemoryAccess(context, false, tmpReg, 0, 0); // false = STR
                    context.NextFreeRegister--;
                }

                EmitOpWithImmediate(context, SyntaxKind.AddExpression, 0, 0, 4);
                EmitStoreToArrayElement(context, size, valReg, 0);
                EmitOpWithImmediate(context, SyntaxKind.SubtractExpression, 0, 0, 4);

                if (targetReg != 0) EmitMovRegister(context, targetReg, 0);
                context.NextFreeRegister--;
                return;
            }

            if (conversion.IsUnboxing) // Wait, if the cast expression is Unboxing?
            {
                Console.WriteLine($"[DEBUG-CONV] UNBOXING on {expr}");
                // Unboxing means we have an object reference (with a header) and want its payload
                int objReg = context.NextFreeRegister++;
                EmitExpressionInternal(context, expr, objReg, tempOffset);

                // For simplicity, we assume we just read offset 4 (after type header)
                var destType = typeInfo.ConvertedType;
                int size = 4;
                if (destType != null)
                {
                    if (destType.SpecialType == SpecialType.System_Byte || destType.SpecialType == SpecialType.System_SByte || destType.SpecialType == SpecialType.System_Boolean) size = 1;
                    else if (destType.SpecialType == SpecialType.System_Int16 || destType.SpecialType == SpecialType.System_UInt16 || destType.SpecialType == SpecialType.System_Char) size = 2;
                }

                int tmpAddr = context.NextFreeRegister++;
                EmitOpWithImmediate(context, SyntaxKind.AddExpression, tmpAddr, objReg, 4);
                EmitLoadFromArrayElement(context, size, targetReg, tmpAddr);
                context.NextFreeRegister--;

                context.NextFreeRegister--;
                return;
            }

            var typeSymbolInfo = context.SemanticModel.GetTypeInfo(expr);
            if (expr is CastExpressionSyntax ce)
            {
                var inputType = context.SemanticModel.GetTypeInfo(ce.Expression).Type;
                var targetType = context.SemanticModel.GetTypeInfo(ce.Type).Type;

                if (inputType != null && targetType != null)
                {
                    var cc = context.SemanticModel.Compilation.ClassifyConversion(inputType, targetType);
                    if (cc.IsUnboxing)
                    {
                        Console.WriteLine($"[DEBUG-CONV] CAST UNBOXING on {expr} from {inputType.Name} to {targetType.Name}");
                        EmitExpressionInternal(context, ce.Expression, targetReg, tempOffset);
                        EmitMemoryAccess(context, true, targetReg, targetReg, 4);
                        return;
                    }
                }
            }

            EmitExpressionInternal(context, expr, targetReg, tempOffset);
        }

        protected virtual void EmitExpressionInternal(MethodCompilationContext context, ExpressionSyntax expr, int targetReg, int tempOffset = 0)
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(expr);
            var possibleMethod = symbolInfo.Symbol as IMethodSymbol ?? context.SemanticModel.GetMemberGroup(expr).FirstOrDefault() as IMethodSymbol;

            if (possibleMethod != null && expr.Parent is not InvocationExpressionSyntax)
            {
                var typeInfo = context.SemanticModel.GetTypeInfo(expr);
                var convertedType = typeInfo.ConvertedType;
                if (convertedType != null && convertedType.TypeKind == TypeKind.Delegate)
                {
                    EmitDelegateCreation(context, possibleMethod, expr, convertedType, targetReg, tempOffset);
                    return;
                }
            }

            var constOpt = context.SemanticModel.GetConstantValue(expr);
            if (constOpt.HasValue)
            {
                if (constOpt.Value == null)
                {
                    EmitMovImmediate(context, targetReg, 0);
                    return;
                }
                else if (constOpt.Value is string stringValue)
                {
                    var stringSymbol = context.Class.Global.RegisterStringLiteral(stringValue);
                    EmitLoadSymbolAddress(context, targetReg, stringSymbol);
                    return;
                }
                else
                {
                    try
                    {
                        int val = Convert.ToInt32(constOpt.Value);
                        EmitMovImmediate(context, targetReg, val);
                        return;
                    }
                    catch { /* fallback if not an integer */ }
                }
            }

            if (expr is LiteralExpressionSyntax literal)
            {
                if (literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    var stringValue = literal.Token.ValueText;
                    var compilationContext = context.Class.Global;

                    var stringSymbol = compilationContext.StringLiterals
                        .FirstOrDefault(kvp => kvp.Value == stringValue).Key;

                    if (stringSymbol == null)
                        throw new InvalidOperationException($"String literal '{stringValue}' was not found in the compilation context.");

                    EmitLoadSymbolAddress(context, targetReg, stringSymbol);
                }
                else
                {
                    // Случай: var a = 10;
                    int value = ParseLiteral(context.Class.Global, literal);
                    EmitMovImmediate(context, targetReg, value);
                }
            }
            else if (expr is IdentifierNameSyntax id)
            {
                // Случай: var a = b;
                if (context.RegisterMap.TryGetValue(id.Identifier.Text, out int srcReg))
                {
                    bool isRef = false;
                    var symbol = context.SemanticModel.GetSymbolInfo(id).Symbol;
                    if (symbol is IParameterSymbol paramSymbol && 
                        (paramSymbol.RefKind == RefKind.Ref || paramSymbol.RefKind == RefKind.Out))
                    {
                        isRef = true;
                    }

                    if (isRef)
                    {
                        // Dereference the pointer: LDR targetReg, [srcReg, #0]
                        EmitMemoryAccess(context, true, targetReg, srcReg, 0);
                    }
                    else
                    {
                        EmitMovRegister(context, targetReg, srcReg);
                    }
                }
                else if (context.StackMap.TryGetValue(id.Identifier.Text, out var sv))
                {
                    EmitMemoryAccess(context, true, targetReg, 13, sv.StackOffset);
                }
            }
            else if (expr is BinaryExpressionSyntax binary)
            {
                if (binary.IsKind(SyntaxKind.AsExpression) || binary.IsKind(SyntaxKind.IsExpression))
                {
                    bool isAs = binary.IsKind(SyntaxKind.AsExpression);
                    var targetType = context.SemanticModel.GetTypeInfo(binary.Right).Type;

                    int objReg = context.NextFreeRegister++;
                    EmitExpressionInternal(context, binary.Left, objReg, tempOffset);

                    string endLabel = context.NextLabel(isAs ? "AS_END" : "IS_END");
                    string falseLabel = context.NextLabel(isAs ? "AS_FALSE" : "IS_FALSE");

                    EmitCompareImmediate(context, objReg, 0);
                    EmitBranch(context, falseLabel, "EQ");

                    if (targetType != null && context.Class.Global.BuildingContext.Options?.TypeHeader == true)
                    {
                        int typeReg = context.NextFreeRegister++;
                        EmitComment(context, "read TypeHeader");
                        EmitMemoryAccess(context, true, typeReg, objReg, 0);

                        string symbolName = context.Class.Global.RegisterTypeLiteral(targetType);

                        int targetTypeReg = context.NextFreeRegister++;
                        EmitLoadSymbolAddress(context, targetTypeReg, symbolName);

                        EmitCompare(context, typeReg, targetTypeReg);
                        EmitBranch(context, falseLabel, "NE");

                        context.NextFreeRegister -= 2;

                        if (isAs)
                        {
                            if (targetReg != objReg) EmitMovRegister(context, targetReg, objReg);
                        }
                        else
                        {
                            EmitMovImmediate(context, targetReg, 1);
                        }
                        EmitJump(context, endLabel);
                    }
                    else
                    {
                        EmitJump(context, falseLabel);
                    }

                    context.MarkLabel(falseLabel);
                    EmitMovImmediate(context, targetReg, 0);

                    context.MarkLabel(endLabel);
                    context.NextFreeRegister--;
                }
                else if (binary.IsKind(SyntaxKind.LogicalAndExpression) ||
                    binary.IsKind(SyntaxKind.LogicalOrExpression) ||
                    binary.IsKind(SyntaxKind.EqualsExpression) ||
                    binary.IsKind(SyntaxKind.NotEqualsExpression) ||
                    binary.IsKind(SyntaxKind.GreaterThanExpression) ||
                    binary.IsKind(SyntaxKind.LessThanExpression) ||
                    binary.IsKind(SyntaxKind.GreaterThanOrEqualExpression) ||
                    binary.IsKind(SyntaxKind.LessThanOrEqualExpression))
                {
                    string trueLabel = context.NextLabel("BOOL_TRUE");
                    string falseLabel = context.NextLabel("BOOL_FALSE");
                    string endLabel = context.NextLabel("BOOL_END");

                    EmitLogicalCondition(context, binary, trueLabel, falseLabel);

                    context.MarkLabel(trueLabel);
                    EmitMovImmediate(context, targetReg, 1);
                    EmitJump(context, endLabel);

                    context.MarkLabel(falseLabel);
                    EmitMovImmediate(context, targetReg, 0);

                    context.MarkLabel(endLabel);
                }
                else
                {
                    // ТЕПЕРЬ МЫ ЗАКРЫВАЕМ ЭТО:
                    EmitArithmetic(binary, targetReg, context, tempOffset);
                }
            }
            else if (expr is ConditionalExpressionSyntax ternary)
            {
                // Создаем уникальные метки для этого конкретного тернарника
                string falseLabel = context.NextLabel("TERN_FALSE");
                string endLabel = context.NextLabel("TERN_END");

                // 1. Вычисляем условие. Если оно ложно — прыгам на falseLabel
                // (Используем нашу готовую логику EmitLogicalCondition)
                EmitLogicalCondition(context, ternary.Condition, "", falseLabel);

                // 2. Ветка TRUE: вычисляем выражение и кладем результат в targetReg
                EmitExpressionValue(context, ternary.WhenTrue, targetReg, tempOffset);

                // Прыгаем в конец, чтобы не выполнять ветку FALSE
                EmitJump(context, endLabel);

                // 3. Ветка FALSE
                context.MarkLabel(falseLabel);
                EmitExpressionValue(context, ternary.WhenFalse, targetReg, tempOffset);

                // 4. Финал
                context.MarkLabel(endLabel);
            }
            else if (expr is ElementAccessExpressionSyntax elementAccess)
            {
                int addrReg = context.NextFreeRegister++;
                EmitAddressOf(context, elementAccess, addrReg, tempOffset);

                var arrayTypeSymbol = context.SemanticModel.GetTypeInfo(elementAccess.Expression).Type as IArrayTypeSymbol;
                var elType = arrayTypeSymbol?.ElementType;

                int elementSize = 4; // Assume 4 bytes for now
                if (elType != null)
                {
                    if (elType.SpecialType == SpecialType.System_Byte || elType.SpecialType == SpecialType.System_SByte || elType.SpecialType == SpecialType.System_Boolean) elementSize = 1;
                    else if (elType.SpecialType == SpecialType.System_Int16 || elType.SpecialType == SpecialType.System_UInt16 || elType.SpecialType == SpecialType.System_Char) elementSize = 2;
                    else if (elType.SpecialType == SpecialType.System_Int64 || elType.SpecialType == SpecialType.System_UInt64 || elType.SpecialType == SpecialType.System_Double) elementSize = 8;
                    else if (elType.TypeKind == TypeKind.Struct)
                        elementSize = Math.Max(1, elType.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Count() * 4);
                }

                EmitLoadFromArrayElement(context, elementSize, targetReg, addrReg);

                context.NextFreeRegister--;
            }
            else if (expr is ArrayCreationExpressionSyntax arrayCreation)
            {
                var typeSymbol = context.SemanticModel.GetTypeInfo(arrayCreation).Type;
                var sizeExpr = arrayCreation.Type.RankSpecifiers.FirstOrDefault()?.Sizes.FirstOrDefault();
                
                int elementSize = 4;
                if (typeSymbol is IArrayTypeSymbol arrayType)
                {
                    var elType = arrayType.ElementType;
                    if (elType.SpecialType == SpecialType.System_Byte || elType.SpecialType == SpecialType.System_SByte || elType.SpecialType == SpecialType.System_Boolean) elementSize = 1;
                    else if (elType.SpecialType == SpecialType.System_Int16 || elType.SpecialType == SpecialType.System_UInt16 || elType.SpecialType == SpecialType.System_Char) elementSize = 2;
                    else if (elType.SpecialType == SpecialType.System_Int64 || elType.SpecialType == SpecialType.System_UInt64 || elType.SpecialType == SpecialType.System_Double) elementSize = 8;
                    else if (elType.TypeKind == TypeKind.Struct)
                        elementSize = Math.Max(1, elType.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Count() * 4);
                }

                bool typeHeader = context.Class.Global.BuildingContext.Options?.TypeHeader == true;
                string symbolName = typeHeader && typeSymbol != null ? context.Class.Global.RegisterTypeLiteral(typeSymbol) : "";

                var info = new ArrayCreationInfo(
                    TypeSymbol: typeSymbol,
                    SizeExpression: sizeExpr,
                    Initializer: arrayCreation.Initializer,
                    ElementSize: elementSize,
                    HasTypeHeader: typeHeader,
                    HeaderSize: typeHeader ? 8 : 4,
                    SymbolName: symbolName,
                    TargetReg: targetReg
                );

                EmitArrayCreation(context, info, tempOffset);
            }
            else if (expr is ImplicitArrayCreationExpressionSyntax implicitArray)
            {
                var typeSymbol = context.SemanticModel.GetTypeInfo(implicitArray).Type;
                int elementSize = 4;
                if (typeSymbol is IArrayTypeSymbol arrayType)
                {
                    var elType = arrayType.ElementType;
                    if (elType.SpecialType == SpecialType.System_Byte || elType.SpecialType == SpecialType.System_SByte || elType.SpecialType == SpecialType.System_Boolean) elementSize = 1;
                    else if (elType.SpecialType == SpecialType.System_Int16 || elType.SpecialType == SpecialType.System_UInt16 || elType.SpecialType == SpecialType.System_Char) elementSize = 2;
                    else if (elType.SpecialType == SpecialType.System_Int64 || elType.SpecialType == SpecialType.System_UInt64 || elType.SpecialType == SpecialType.System_Double) elementSize = 8;
                    else if (elType.TypeKind == TypeKind.Struct)
                        elementSize = Math.Max(1, elType.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Count() * 4);
                }

                bool typeHeader = context.Class.Global.BuildingContext.Options?.TypeHeader == true;
                string symbolName = typeHeader && typeSymbol != null ? context.Class.Global.RegisterTypeLiteral(typeSymbol) : "";

                var info = new ArrayCreationInfo(
                    TypeSymbol: typeSymbol,
                    SizeExpression: null,
                    Initializer: implicitArray.Initializer,
                    ElementSize: elementSize,
                    HasTypeHeader: typeHeader,
                    HeaderSize: typeHeader ? 8 : 4,
                    SymbolName: symbolName,
                    TargetReg: targetReg
                );

                EmitArrayCreation(context, info, tempOffset);
            }
            else if (expr is ObjectCreationExpressionSyntax objectCreation)
            {
                var typeSymbol = context.SemanticModel.GetTypeInfo(objectCreation).Type;
                
                if (typeSymbol != null && typeSymbol.TypeKind == TypeKind.Delegate)
                {
                    var ctorArg = objectCreation.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                    var symInfo = ctorArg != null ? context.SemanticModel.GetSymbolInfo(ctorArg).Symbol : null;
                    var memGrp = ctorArg != null ? context.SemanticModel.GetMemberGroup(ctorArg).FirstOrDefault() : null;

                    var targetMethodCtor = symInfo as IMethodSymbol ?? memGrp as IMethodSymbol;
                    if (targetMethodCtor != null)
                    {
                        EmitDelegateCreation(context, targetMethodCtor, ctorArg, typeSymbol, targetReg, tempOffset);
                        return;
                    }
                }

                int size = 4;
                bool hasHeader = false;
                if (typeSymbol != null)
                {
                    hasHeader = context.Class.Global.BuildingContext.Options?.TypeHeader == true && typeSymbol.IsReferenceType;
                    if (hasHeader) size = 4; else size = 0;
                    int fieldsCount = typeSymbol.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Count();
                    size += fieldsCount * 4;
                    if (size == 0) size = 4; 
                }

                var ctorSymbol = context.SemanticModel.GetSymbolInfo(objectCreation).Symbol as IMethodSymbol;
                string symbolName = hasHeader && typeSymbol != null ? context.Class.Global.RegisterTypeLiteral(typeSymbol) : "";

                var info = new ObjectCreationInfo(
                    TypeSymbol: typeSymbol,
                    CtorSymbol: ctorSymbol,
                    Arguments: objectCreation.ArgumentList?.Arguments,
                    HasTypeHeader: hasHeader,
                    Size: size,
                    SymbolName: symbolName,
                    TargetReg: targetReg
                );

                EmitObjectCreation(context, info, tempOffset);
            }
            else if (expr is InvocationExpressionSyntax invocation)
            {
                var methodSymbol = context.SemanticModel.GetSymbolInfo(invocation).Symbol as Microsoft.CodeAnalysis.IMethodSymbol 
                                ?? context.SemanticModel.GetMemberGroup(invocation).FirstOrDefault() as Microsoft.CodeAnalysis.IMethodSymbol;

                var delegateType = context.SemanticModel.GetTypeInfo(invocation.Expression).Type;
                bool isDelegateInvoke = delegateType != null && delegateType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Delegate;
                if (!isDelegateInvoke && methodSymbol != null && methodSymbol.ContainingType?.TypeKind == Microsoft.CodeAnalysis.TypeKind.Delegate)
                {
                    isDelegateInvoke = true; 
                }

                bool isBaseCall = false;
                bool isInterfaceCall = false;
                bool isVirtualCall = false;
                string nativeFunctionName = null;
                
                if (methodSymbol != null)
                {
                    isBaseCall = invocation.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax m && m.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.BaseExpressionSyntax;
                    isInterfaceCall = !methodSymbol.IsStatic && !isBaseCall && methodSymbol.ContainingType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface;
                    isVirtualCall = !methodSymbol.IsStatic && !isBaseCall && !isInterfaceCall && 
                                         (methodSymbol.IsVirtual || methodSymbol.IsAbstract || methodSymbol.IsOverride);
                    
                    nativeFunctionName = System.Linq.Enumerable.FirstOrDefault(methodSymbol.GetAttributes(), a => a.AttributeClass?.Name.Contains("NativeCall") == true)?
                        .ConstructorArguments.FirstOrDefault().Value?.ToString();
                }

                var info = new InvocationInfo(
                    MethodSymbol: methodSymbol,
                    InstanceExpression: invocation.Expression,
                    Arguments: invocation.ArgumentList?.Arguments,
                    IsDelegateInvoke: isDelegateInvoke,
                    IsInterfaceCall: isInterfaceCall,
                    IsVirtualCall: isVirtualCall,
                    IsBaseCall: isBaseCall,
                    NativeFunctionName: nativeFunctionName,
                    TargetReg: targetReg
                );

                EmitInvocation(context, info, tempOffset);
            }
        }

        
        public abstract void EmitInvocation(MethodCompilationContext context, InvocationInfo info, int tempOffset);
        public abstract void EmitObjectCreation(MethodCompilationContext context, ObjectCreationInfo info, int tempOffset);
        public abstract void EmitArrayCreation(MethodCompilationContext context, ArrayCreationInfo info, int tempOffset);
        //public abstract void EmitDelegateCreation(MethodCompilationContext context, IMethodSymbol targetMethod, ExpressionSyntax nodeExpr, ITypeSymbol delegateType, int targetReg, int tempOffset);

        public abstract void EmitCall(MethodCompilationContext context, string name, bool isStatic, bool isNative = false);
        public abstract void EmitMovImmediate(MethodCompilationContext context, int reg, int val);
        public abstract void EmitCompare(MethodCompilationContext context, int left, int right);
        public abstract void EmitBranch(MethodCompilationContext context, string label, string condition);
        public abstract void EmitMovRegister(MethodCompilationContext context, int target, int source);
        public abstract void EmitMemoryAccess(MethodCompilationContext context, bool isLoad, int targetReg, int baseReg, int offset);
        public abstract void EmitArithmeticOp(MethodCompilationContext context, SyntaxKind op, int target, int left, int right);
        public abstract void EmitOpWithImmediate(MethodCompilationContext context, SyntaxKind op, int target, int left, int value);
        public abstract void EmitAddSP(MethodCompilationContext context, int targetReg, int offset);
        
        public virtual void EmitAddressOf(MethodCompilationContext context, ExpressionSyntax expr, int targetReg, int tempOffset = 0)
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(expr).Symbol;

            if (expr is IdentifierNameSyntax id)
            {
                string varName = id.Identifier.Text;
                if (context.StackMap.TryGetValue(varName, out var stackVar))
                {
                    int offset = stackVar.StackOffset;
                    EmitAddSP(context, targetReg, offset);
                    return;
                }
            }
            else if (expr is MemberAccessExpressionSyntax memberAccess && symbolInfo is IFieldSymbol fieldSymbol)
            {
                string typeName = fieldSymbol.ContainingType.ToDisplayString();
                if (context.Class.Global.Childs.TryGetValue(typeName, out var typeCtx) && typeCtx is TypeCompilationContext tcc)
                {
                    if (tcc.FieldOffsets.TryGetValue(fieldSymbol.Name, out int fieldOffset))
                    {
                        string structName = memberAccess.Expression.ToString();
                        int baseReg = tempOffset + 1;

                        if (context.StackMap.TryGetValue(structName, out var stackVar))
                        {
                            // SP + stackVar.StackOffset + fieldOffset
                            int totalOffset = stackVar.StackOffset + fieldOffset;
                            EmitAddSP(context, targetReg, totalOffset);
                        }
                        else
                        {
                            EmitExpressionValue(context, memberAccess.Expression, baseReg);
                            EmitOpWithImmediate(context, SyntaxKind.AddExpression, targetReg, baseReg, fieldOffset);
                        }
                        return;
                    }
                }
            }

            else if (expr is ElementAccessExpressionSyntax elementAccess)
            {
                // �������� ������ �������/���������
                EmitExpressionValue(context, elementAccess.Expression, targetReg);

                // ���������� �������
                int indexReg = context.NextFreeRegister++;
                var arg = elementAccess.ArgumentList.Arguments[0]; // ���� 1D �������
                EmitExpressionValue(context, arg.Expression, indexReg);

                // Check bounds: index >= Length -> Trap
                int lengthReg = context.NextFreeRegister++;
                bool typeHeader = context.Class.Global.BuildingContext.Options?.TypeHeader == true;
                int lengthOffset = typeHeader ? 4 : 0;

                EmitComment(context, "Load Array Length");
                EmitMemoryAccess(context, true, lengthReg, targetReg, lengthOffset);

                string okLabel = context.NextLabel("BOUNDS_OK");
                EmitCompare(context, indexReg, lengthReg);
                EmitBranch(context, okLabel, "CC");
                EmitMovImmediate(context, 0, 0);
                EmitCall(context, "NETMCU_Throw", isStatic: true, isNative: true);

                context.MarkLabel(okLabel);

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

                int elemSizeReg = context.NextFreeRegister++;
                EmitMovImmediate(context, elemSizeReg, elementSize);

                EmitArithmeticOp(context, SyntaxKind.MultiplyExpression, indexReg, indexReg, elemSizeReg);

                int headerSize = context.Class.Global.BuildingContext.Options?.TypeHeader == true ? 8 : 0;
                if (headerSize > 0)
                {
                    EmitOpWithImmediate(context, SyntaxKind.AddExpression, indexReg, indexReg, headerSize);
                }

                EmitArithmeticOp(context, SyntaxKind.AddExpression, targetReg, targetReg, indexReg);

                context.NextFreeRegister -= 3; // free elemSizeReg, lengthReg, indexReg
                return;
            }

            throw new Exception($"Cannot take address of expr: {expr}");
        }

        public abstract void EmitCompareImmediate(MethodCompilationContext context, int reg, int imm);

        public abstract void EmitLoadSymbolAddress(MethodCompilationContext context, int targetReg, string symbolName);
        public abstract void EmitPush(MethodCompilationContext context, int reg);
        public abstract void EmitAdjustSP(MethodCompilationContext context, int offset);
        public abstract void EmitCallRegister(MethodCompilationContext context, int reg);

        public abstract void ResolveJumps(MethodCompilationContext context);
        public abstract void PatchCall(byte[] binary, int offset, int jumpOffset);
        public abstract void PatchDataAddress(byte[] binary, int offset, uint value);

        public virtual void EmitComment(MethodCompilationContext context, string comment)
        {
            // Default no-op. Backends can override to write comments (e.g. context.Emit($"@ {comment}"))
        }

        public virtual void CompileMethod(MethodCompilationContext method)
        {
            if (method.NativeName != null) return;

            var methodSyntax = method.MethodSyntax as MethodDeclarationSyntax;
            var localFuncSyntax = method.MethodSyntax as LocalFunctionStatementSyntax;

            var body = methodSyntax?.Body ?? localFuncSyntax?.Body;
            var expressionBody = methodSyntax?.ExpressionBody ?? localFuncSyntax?.ExpressionBody;

            if (body == null && expressionBody == null)
            {
                return;
            }
            var modifiers = methodSyntax?.Modifiers ?? localFuncSyntax?.Modifiers;

            // СБОР ЛОКАЛЬНЫХ КОНСТАНТ МЕТОДА
            var localConsts = method.MethodSyntax.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
                                .Where(s => s.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)));

            foreach (var localConst in localConsts)
            {
                foreach (var v in localConst.Declaration.Variables)
                {
                    if (v.Initializer?.Value is LiteralExpressionSyntax lit)
                        method.RegisterConstant(v.Identifier.Text, method.Class.Global.Backend.ParseLiteral(method.Class.Global, lit));
                }
            }

            var methodSymbol = method.SemanticModel.GetDeclaredSymbol(method.MethodSyntax) as IMethodSymbol;

            bool isStatic = modifiers.Value.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            var parameters = methodSymbol.Parameters;

            var declarations = method.MethodSyntax.DescendantNodes().OfType<VariableDeclarationSyntax>();
            foreach (var decl in declarations)
            {
                var typeSymbol = method.SemanticModel.GetTypeInfo(decl.Type).Type;
                string typeName = typeSymbol?.ToDisplayString() ?? decl.Type.ToString();

                foreach (var v in decl.Variables)
                {
                    method.AllocateOnStack(v.Identifier.Text, typeName);
                }
            }

            GenerateMethodPrologue(method, !isStatic, parameters);

            var builder = new MethodAstVisitor(method);
            if (body != null)
                builder.Visit(body);
            if (expressionBody != null)
                builder.Visit(expressionBody);

            GenerateMethodEpilogue(method);
            ResolveJumps(method);
        }
    }
}