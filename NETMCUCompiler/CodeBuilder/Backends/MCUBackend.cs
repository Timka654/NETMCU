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

        public abstract void GenerateIfStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateTrueBlock, Action generateFalseBlock);

        public abstract void GenerateWhileStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateBody, Action<string, string> registerLoopContext, Action popLoopContext);

        public abstract void GenerateDoStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateBody, Action<string, string> registerLoopContext, Action popLoopContext);

        public abstract void GenerateForStatement(MethodCompilationContext context, ExpressionSyntax condition, Action generateInit, Action generateBody, Action generateIncrementor, Action<string, string> registerLoopContext, Action popLoopContext);

        public abstract void GenerateForEachStatement(MethodCompilationContext context, ForEachStatementSyntax node, Action generateBody, Action<string, string> registerLoopContext, Action popLoopContext);

        public abstract void GenerateBreakStatement(MethodCompilationContext context, string breakLabel);

        public abstract void GenerateContinueStatement(MethodCompilationContext context, string continueLabel);

        public abstract void GenerateTryStatement(MethodCompilationContext context, Action generateTryBlock, Action<CatchClauseSyntax> generateCatchBlock, Action generateFinallyBlock, SyntaxList<CatchClauseSyntax> catches, FinallyClauseSyntax finallyClause);

        public abstract void GenerateThrowStatement(MethodCompilationContext context, ExpressionSyntax expression);

        public abstract void GenerateReturnStatement(MethodCompilationContext context, ExpressionSyntax expression);

        public abstract void GenerateSwitchStatement(MethodCompilationContext context, ExpressionSyntax expression, SyntaxList<SwitchSectionSyntax> sections, Action<SwitchSectionSyntax> generateSectionBody, Action<string, string> registerLoopContext, Action popLoopContext);

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

                    ASMInstructions.EmitExpression(variable.Initializer.Value, initValueReg, context, 0);
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

        public virtual void GenerateAssignmentExpression(MethodCompilationContext context, AssignmentExpressionSyntax node)
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
                        ASMInstructions.EmitArithmeticOp(opKind, tmpRead, tmpRead, valueReg, context);
                        EmitStoreToArrayElement(context, elementSize, tmpRead, destAddrReg);
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

        public abstract void GeneratePrefixUnaryExpression(MethodCompilationContext context, PrefixUnaryExpressionSyntax node);

        public abstract void GeneratePostfixUnaryExpression(MethodCompilationContext context, PostfixUnaryExpressionSyntax node);

        public abstract void GenerateLiteralExpression(MethodCompilationContext context, LiteralExpressionSyntax node);

        public abstract void GenerateIdentifierName(MethodCompilationContext context, IdentifierNameSyntax node);

        public abstract void GenerateInvocationExpression(MethodCompilationContext context, InvocationExpressionSyntax node);

        public abstract void EmitJump(MethodCompilationContext context, string label);
        public abstract void EmitLogicalCondition(MethodCompilationContext context, ExpressionSyntax condition, string trueLabel, string falseLabel);
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
    }
}